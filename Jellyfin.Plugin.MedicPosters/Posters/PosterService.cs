using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MedicPosters.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>Where a run has got to, for the settings page.</summary>
public class RunProgress
{
    public int Total { get; set; }

    public int Done { get; set; }

    public string? Current { get; set; }
}

/// <summary>
/// Finds the films, series and libraries, makes their posters and covers and swaps them in, and puts their own
/// pictures back. An item's own poster file is never changed or deleted: Medic Posters only points Jellyfin at
/// another picture, and remembers the old one.
/// </summary>
public class PosterService
{
    public const string PosterKind = "poster";
    public const string CoverKind = "cover";

    // Libraries with videos to cut moments from. Music, books, photos, Live TV and the like have none.
    private static readonly HashSet<CollectionTypeOptions?> VideoLibraryTypes = new()
    {
        CollectionTypeOptions.movies,
        CollectionTypeOptions.tvshows,
        CollectionTypeOptions.homevideos,
        CollectionTypeOptions.musicvideos,
        null // "Mixed content"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly PosterMaker _maker;
    private readonly PosterStore _store;
    private readonly ILogger<PosterService> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private volatile RunProgress? _progress;

    public PosterService(ILibraryManager libraryManager, PosterMaker maker, PosterStore store, ILogger<PosterService> logger)
    {
        _libraryManager = libraryManager;
        _maker = maker;
        _store = store;
        _logger = logger;
    }

    /// <summary>Bytes per pixel per frame for a GIF when nothing has been made yet to measure.</summary>
    public const double DefaultBytesPerPixelFrame = 0.05;

    /// <summary>Set by "Make them all again": the next run also redoes items that already have one.</summary>
    public bool RedoAllNextRun { get; set; }

    /// <summary>What the next run makes: <see cref="PosterKind"/>, <see cref="CoverKind"/>, or null for both (the nightly run).</summary>
    public string? NextRunKind { get; set; }

    /// <summary>The item and spec the preview file was last made for, so "Use this one" can apply it.</summary>
    public (Guid ItemId, string Kind, PosterSpec Spec)? LastPreview { get; private set; }

    public RunProgress? Progress => _progress;

    /// <summary>Every library on the server with videos in it: films, TV, home videos, music videos and mixed.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Libraries() =>
        _libraryManager.GetVirtualFolders()
            .Where(f => VideoLibraryTypes.Contains(f.CollectionType) && Guid.TryParse(f.ItemId, out _))
            .Select(f => (Guid.Parse(f.ItemId), f.Name))
            .ToList();

    /// <summary>The libraries to make posters for: all of them, or the ones chosen in 1.0 and 1.1, less the ones left out.</summary>
    public IReadOnlyList<CollectionFolder> ChosenLibraries()
    {
        var config = Plugin.Instance?.Configuration;
        var chosen = Ids(config?.LibraryIds);
        var excluded = Ids(config?.ExcludedLibraryIds);
        return Libraries()
            .Where(l => (chosen.Count == 0 || chosen.Contains(l.Id)) && !excluded.Contains(l.Id))
            .Select(l => _libraryManager.GetItemById(l.Id))
            .OfType<CollectionFolder>()
            .ToList();
    }

    private static HashSet<Guid> Ids(string[]? ids) =>
        (ids ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

    /// <summary>The films, series, home videos and music videos in the chosen libraries: everything that gets a poster.</summary>
    public IReadOnlyList<BaseItem> Titles()
    {
        // A library's items sit under its folders on disk, which is what Jellyfin files them by, not the library itself.
        var folders = ChosenLibraries().SelectMany(f => f.PhysicalFolderIds).Distinct().ToArray();
        if (folders.Length == 0)
        {
            return Array.Empty<BaseItem>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Video, BaseItemKind.MusicVideo },
            TopParentIds = folders,
            Recursive = true,
            IsVirtualItem = false
        }).Where(i => i.ExtraType is null).ToList();
    }

    /// <summary>Why a video can't have moments cut from it, or null if it can.</summary>
    public static string? CantMake(BaseItem video)
    {
        if (string.IsNullOrEmpty(video.Path) || video.IsShortcut || !File.Exists(video.Path))
        {
            return "It isn't a video file on this server (a stream, a disc folder or a missing file).";
        }

        return null;
    }

    /// <summary>
    /// The moments for an item's poster: spread through a film, or one from each of a few episodes of a series.
    /// Returns why not when there's nothing to cut them from.
    /// </summary>
    public (IReadOnlyList<Clip> Clips, string? Error) PosterClips(BaseItem item, PosterSpec spec)
    {
        if (item is Series series)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                Recursive = true,
                IsVirtualItem = false
            });
            var clips = OneMomentEach(episodes, spec);
            return clips.Count == 0 ? (clips, "None of its episodes is a video file on this server.") : (clips, null);
        }

        if (CantMake(item) is { } why)
        {
            return (Array.Empty<Clip>(), why);
        }

        var starts = PosterMaker.MomentStarts(Seconds(item), spec.Moments, spec.MomentSeconds);
        return (starts.Select(s => new Clip(item.Path, s, IsHdr(item))).ToList(), null);
    }

    /// <summary>The moments for a library's cover: one from each of a few random videos in it.</summary>
    public (IReadOnlyList<Clip> Clips, string? Error) CoverClips(CollectionFolder library, PosterSpec spec)
    {
        var folders = library.PhysicalFolderIds.ToArray();
        var videos = folders.Length == 0 ? new List<BaseItem>() : _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo },
            TopParentIds = folders,
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            // Enough to find a few readable files among streams and missing ones, without listing a whole TV library.
            Limit = 60
        });
        var clips = OneMomentEach(videos, spec);
        return clips.Count == 0 ? (clips, "Nothing in it is a video file on this server.") : (clips, null);
    }

    // A moment from each of up to spec.Moments readable videos, in random order.
    private static List<Clip> OneMomentEach(IEnumerable<BaseItem> videos, PosterSpec spec)
    {
        return videos
            .Where(v => CantMake(v) is null && v.ExtraType is null)
            .OrderBy(_ => Random.Shared.Next())
            .Take(spec.Moments)
            .Select(v => new Clip(v.Path, PosterMaker.RandomStart(Seconds(v), spec.MomentSeconds, Random.Shared), IsHdr(v)))
            .ToList();
    }

    /// <summary>
    /// Makes posters for every title that needs one, and covers for the chosen libraries. Items with a Medic
    /// picture are skipped, and so are items whose picture was changed by hand (or by a metadata refresh) since,
    /// unless <see cref="RedoAllNextRun"/> is set.
    /// </summary>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        bool redo = RedoAllNextRun;
        string? only = NextRunKind;
        RedoAllNextRun = false;
        NextRunKind = null;
        var summary = new RunSummary { StartedUtc = DateTime.UtcNow };
        try
        {
            var config = Plugin.Instance!.Configuration;
            var posterSpec = PosterSpec.From(config);
            var coverSpec = PosterSpec.CoverFrom(config);
            // The nightly run makes covers only when they're switched on; "Make library covers now" always does.
            bool covers = only == CoverKind || (only is null && config.MakeLibraryCovers);
            bool posters = only != CoverKind;
            var jobs = ChosenLibraries().Where(_ => covers).Select(l => ((BaseItem)l, CoverKind))
                .Concat(Titles().Where(_ => posters).Select(t => (t, PosterKind)))
                .ToList();
            _progress = new RunProgress { Total = jobs.Count };
            _logger.LogInformation("Medic Posters: checking {Count} titles and libraries{Redo}", jobs.Count, redo ? ", making every one again" : string.Empty);

            for (int i = 0; i < jobs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (item, kind) = jobs[i];
                _progress = new RunProgress { Total = jobs.Count, Done = i, Current = item.Name };
                // A metadata refresh may have swapped the item's own poster back in while Medic Posters wasn't watching.
                await KeepOursAsync(item.Id, ct).ConfigureAwait(false);
                var record = _store.Read(s => s.Posters.GetValueOrDefault(item.Id));
                string? current = (_libraryManager.GetItemById(item.Id) ?? item).GetImageInfo(ImageType.Primary, 0)?.Path;
                bool hasOurs = record is not null && _store.IsOurs(current) && File.Exists(current);
                bool changedElsewhere = record is not null && !_store.IsOurs(current);

                if (!redo && (hasOurs || changedElsewhere))
                {
                    if (changedElsewhere)
                    {
                        summary.LeftAlone++;
                    }
                }
                else
                {
                    var spec = kind == CoverKind ? coverSpec : posterSpec;
                    string? error = await MakeForAsync(item, kind, spec, ct).ConfigureAwait(false);
                    if (error is null)
                    {
                        summary.Made++;
                    }
                    else
                    {
                        summary.Failed++;
                    }
                }

                progress?.Report(100.0 * (i + 1) / jobs.Count);
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
            throw;
        }
        finally
        {
            summary.FinishedUtc = DateTime.UtcNow;
            _store.Update(s => s.LastRun = summary);
            _progress = null;
            _running.Release();
            _logger.LogInformation("Medic Posters: made {Made}, failed {Failed}, left {LeftAlone} alone", summary.Made, summary.Failed, summary.LeftAlone);
        }
    }

    /// <summary>Makes one poster or library cover and swaps it in. Returns null when it worked, otherwise why not.</summary>
    public async Task<string?> MakeForAsync(BaseItem item, string kind, PosterSpec spec, CancellationToken ct)
    {
        var (clips, error) = kind == CoverKind && item is CollectionFolder library ? CoverClips(library, spec) : PosterClips(item, spec);
        if (error is null)
        {
            string output = _store.PosterPath(item.Id);
            string temp = output + ".new.gif";
            error = await _maker.MakeAsync(clips, item.Name, spec, temp, ct).ConfigureAwait(false);
            if (error is null)
            {
                File.Move(temp, output, overwrite: true);
                await UsePosterAsync(item, kind, output, spec.Width, spec.Height, spec.Frames, ct).ConfigureAwait(false);
                _store.Update(s => s.Failures.Remove(item.Id));
                return null;
            }

            try { File.Delete(temp); } catch (IOException) { }
        }

        _store.Update(s => s.Failures[item.Id] = new PosterFailure { ItemId = item.Id, Title = item.Name, Reason = error, Utc = DateTime.UtcNow });
        return error;
    }

    /// <summary>
    /// Makes a poster (or, for <see cref="CoverKind"/>, a library cover) into the preview file, without changing
    /// anything. Uses the given item, or a random one. Returns the item used, or why it didn't work.
    /// </summary>
    public async Task<(BaseItem? Item, string? Error)> PreviewAsync(Guid? itemId, string kind, PluginConfiguration? settings, CancellationToken ct)
    {
        // The page sends the settings on screen, saved or not, so the preview shows what they'd make.
        var config = settings ?? Plugin.Instance!.Configuration;
        bool cover = kind == CoverKind;
        var spec = cover ? PosterSpec.CoverFrom(config) : PosterSpec.From(config);
        IReadOnlyList<BaseItem> candidates = cover ? ChosenLibraries() : Titles();
        BaseItem? item = itemId is { } id && id != Guid.Empty
            ? candidates.FirstOrDefault(c => c.Id == id)
            : candidates.OrderBy(_ => Random.Shared.Next()).FirstOrDefault(c => (cover ? CoverClips((CollectionFolder)c, spec) : PosterClips(c, spec)).Error is null);
        if (item is null)
        {
            return (null, candidates.Count == 0
                ? (cover ? "There are no libraries with videos to choose from." : "There are no films or series in the chosen libraries.")
                : "Nothing in the chosen libraries is a video file this server can read.");
        }

        var (clips, error) = cover ? CoverClips((CollectionFolder)item, spec) : PosterClips(item, spec);
        LastPreview = null;
        error ??= await _maker.MakeAsync(clips, item.Name, spec, _store.PreviewPath, ct).ConfigureAwait(false);
        if (error is null)
        {
            LastPreview = (item.Id, kind, spec);
        }

        return (item, error);
    }

    /// <summary>Uses the last preview as that item's poster or cover. Returns the item, or why it couldn't.</summary>
    public async Task<(BaseItem? Item, string? Error)> UsePreviewAsync(CancellationToken ct)
    {
        if (LastPreview is not { } last || !File.Exists(_store.PreviewPath))
        {
            return (null, "Make a preview first.");
        }

        if (_libraryManager.GetItemById(last.ItemId) is not BaseItem item)
        {
            return (null, "That film, series or library isn't on the server any more.");
        }

        string output = _store.PosterPath(item.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(_store.PreviewPath, output, overwrite: true);
        await UsePosterAsync(item, last.Kind, output, last.Spec.Width, last.Spec.Height, last.Spec.Frames, ct).ConfigureAwait(false);
        _store.Update(s => s.Failures.Remove(item.Id));
        return (item, null);
    }

    /// <summary>Titles in the chosen libraries whose name contains the text, for picking one to preview or make.</summary>
    public IReadOnlyList<BaseItem> FindTitles(string text, int limit) =>
        Titles().Where(t => !t.IsShortcut && t.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

    /// <summary>Puts a film's own poster back and deletes Medic's. Returns false if Medic never made one for it.</summary>
    public async Task<bool> RestoreAsync(Guid itemId, CancellationToken ct)
    {
        var record = _store.Read(s => s.Posters.GetValueOrDefault(itemId));
        if (record is null)
        {
            return false;
        }

        if (_libraryManager.GetItemById(itemId) is BaseItem film)
        {
            var current = film.GetImageInfo(ImageType.Primary, 0);
            // Only undo Medic's own poster: one chosen by hand since then stays.
            if (current is null || _store.IsOurs(current.Path))
            {
                var images = film.ImageInfos.Where(i => i.Type != ImageType.Primary || !_store.IsOurs(i.Path)).ToList();
                if (record.Original is { } o)
                {
                    images.Add(new ItemImageInfo { Type = ImageType.Primary, Path = o.Path, DateModified = o.DateModified, Width = o.Width, Height = o.Height, BlurHash = o.BlurHash });
                }

                film.ImageInfos = images.ToArray();
                await film.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            }
        }

        try { File.Delete(_store.PosterPath(itemId)); } catch (IOException) { }
        _store.Update(s => s.Posters.Remove(itemId));
        return true;
    }

    /// <summary>Puts every film's own poster back. Returns how many films were changed.</summary>
    public async Task<int> RestoreAllAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int count = 0;
            foreach (var id in _store.Read(s => s.Posters.Keys.ToList()))
            {
                ct.ThrowIfCancellationRequested();
                if (await RestoreAsync(id, ct).ConfigureAwait(false))
                {
                    count++;
                }
            }

            _store.Update(s => s.Failures.Clear());
            return count;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Puts Medic's poster back when a metadata refresh has swapped in the film's own poster again (Jellyfin reads
    /// poster.jpg and the like from the film's folder on every refresh). Any other poster is someone's new choice and
    /// stays. Returns whether Medic's poster was put back.
    /// </summary>
    public async Task<bool> KeepOursAsync(Guid itemId, CancellationToken ct)
    {
        var record = _store.Read(s => s.Posters.GetValueOrDefault(itemId));
        string ours = _store.PosterPath(itemId);
        if (record?.Original is null || !File.Exists(ours) || _libraryManager.GetItemById(itemId) is not BaseItem film)
        {
            return false;
        }

        var current = film.GetImageInfo(ImageType.Primary, 0);
        // The same file, unchanged: an upload under Edit Images is saved under the same name, but is a new file.
        if (current is null || _store.IsOurs(current.Path) || !SamePath(current.Path, record.Original.Path)
            || Math.Abs((current.DateModified - record.Original.DateModified).TotalSeconds) > 2)
        {
            return false;
        }

        _logger.LogInformation("Medic Posters: a refresh put back {Title}'s own poster; using the animated one again", film.Name);
        await UsePosterAsync(film, record.Kind, ours, record.Width, record.Height, record.Frames, ct).ConfigureAwait(false);
        return true;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task UsePosterAsync(BaseItem film, string kind, string path, int width, int height, int frames, CancellationToken ct)
    {
        var current = film.GetImageInfo(ImageType.Primary, 0);
        var existing = _store.Read(s => s.Posters.GetValueOrDefault(film.Id));

        // The film's own poster: the one it has now, unless that's already Medic's.
        SavedImage? original = existing?.Original;
        if (current is not null && !_store.IsOurs(current.Path))
        {
            original = new SavedImage { Path = current.Path, DateModified = current.DateModified, Width = current.Width, Height = current.Height, BlurHash = current.BlurHash };
        }

        var poster = new ItemImageInfo
        {
            Type = ImageType.Primary,
            Path = path,
            DateModified = File.GetLastWriteTimeUtc(path),
            Width = width,
            Height = height
        };
        film.ImageInfos = film.ImageInfos.Where(i => i.Type != ImageType.Primary).Append(poster).ToArray();
        await film.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);

        _store.Update(s => s.Posters[film.Id] = new PosterRecord
        {
            ItemId = film.Id,
            Title = film.Name,
            Kind = kind,
            MadeUtc = DateTime.UtcNow,
            Bytes = new FileInfo(path).Length,
            Width = width,
            Height = height,
            Frames = frames,
            Original = original
        });
    }

    private static double? Seconds(BaseItem film) => film.RunTimeTicks is > 0 ? film.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : null;

    private static bool IsHdr(BaseItem film)
    {
        var video = film.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return video is not null && video.VideoRange == VideoRange.HDR;
    }
}

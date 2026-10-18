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
    /// <summary>"make" while posters are being made, "restore" while their own pictures are being put back.</summary>
    public string Kind { get; set; } = "make";

    public int Total { get; set; }

    public int Done { get; set; }

    public string? Current { get; set; }

    /// <summary>Roughly how long the rest will take, once one has been made to time; null before that.</summary>
    public double? SecondsLeft { get; set; }
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
    public IReadOnlyList<CollectionFolder> ChosenLibraries(PluginConfiguration? settings = null)
    {
        var config = settings ?? Plugin.Instance?.Configuration;
        var chosen = Ids(config?.LibraryIds);
        var excluded = Ids(config?.ExcludedLibraryIds);
        return Libraries()
            .Where(l => (chosen.Count == 0 || chosen.Contains(l.Id)) && !excluded.Contains(l.Id))
            .Select(l => _libraryManager.GetItemById(l.Id))
            .OfType<CollectionFolder>()
            .ToList();
    }

    /// <summary>
    /// The libraries that get a cover: the chosen ones, and any library whose cover has its own moments or takes
    /// them from another library, even one left out of posters, such as an IPTV library of streams.
    /// </summary>
    public IReadOnlyList<CollectionFolder> CoverTargets(PluginConfiguration? settings = null)
    {
        var config = settings ?? Plugin.Instance?.Configuration;
        var chosen = ChosenLibraries(config);
        var extra = Libraries()
            .Where(l => chosen.All(c => c.Id != l.Id) && config?.CustomFor(l.Id) is { } custom
                && (!string.IsNullOrEmpty(custom.SourceLibraryId) || custom.Moments.Length > 0))
            .Select(l => _libraryManager.GetItemById(l.Id))
            .OfType<CollectionFolder>();
        return chosen.Concat(extra).ToList();
    }

    /// <summary>The text on an item's poster or cover: its own text if it has some, otherwise its name.</summary>
    public static string TitleFor(BaseItem item, PluginConfiguration? config) =>
        config?.CustomFor(item.Id)?.Title is { Length: > 0 } title && !string.IsNullOrWhiteSpace(title) ? title.Trim() : item.Name;

    private static HashSet<Guid> Ids(string[]? ids) =>
        (ids ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

    /// <summary>The films, series, home videos and music videos in the chosen libraries: everything that gets a poster.</summary>
    public IReadOnlyList<BaseItem> Titles(PluginConfiguration? settings = null) => TitlesIn(ChosenLibraries(settings));

    private IReadOnlyList<BaseItem> TitlesIn(IEnumerable<CollectionFolder> libraries)
    {
        // A library's items sit under its folders on disk, which is what Jellyfin files them by, not the library itself.
        var folders = libraries.SelectMany(f => f.PhysicalFolderIds).Distinct().ToArray();
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
    public (IReadOnlyList<Clip> Clips, string? Error) PosterClips(BaseItem item, PosterSpec spec, PluginConfiguration? config = null)
    {
        if (ChosenMoments(item.Id, spec, config) is { } chosen)
        {
            return chosen;
        }

        if (item is Series series)
        {
            var clips = OneMomentEach(Episodes(series), spec);
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
    public (IReadOnlyList<Clip> Clips, string? Error) CoverClips(CollectionFolder library, PosterSpec spec, PluginConfiguration? config = null)
    {
        if (ChosenMoments(library.Id, spec, config) is { } chosen)
        {
            return chosen;
        }

        // An IPTV library has nothing to cut moments from, so its cover can take them from another library.
        var custom = config?.CustomFor(library.Id);
        var source = custom?.SourceLibraryId is { } sourceId && Guid.TryParse(sourceId, out var sid)
            && _libraryManager.GetItemById(sid) is CollectionFolder other ? other : library;
        var folders = source.PhysicalFolderIds.ToArray();
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
        return clips.Count == 0
            ? (clips, source == library ? "Nothing in it is a video file on this server." : $"Nothing in {source.Name} is a video file on this server.")
            : (clips, null);
    }

    /// <summary>The moments picked by hand for a poster or cover, in order, or null when there are none.</summary>
    private (IReadOnlyList<Clip> Clips, string? Error)? ChosenMoments(Guid itemId, PosterSpec spec, PluginConfiguration? config)
    {
        var picked = config?.CustomFor(itemId)?.Moments;
        if (picked is null || picked.Length == 0)
        {
            return null;
        }

        var clips = picked.Take(MaxMoments).Select(m => MomentClip(m, spec)).OfType<Clip>().ToList();
        return clips.Count == 0 ? (clips, "None of the moments picked for it is in a video file this server can read.") : (clips, null);
    }

    /// <summary>Most moments a poster or cover can have, picked by hand or not.</summary>
    public const int MaxMoments = 6;

    private Clip? MomentClip(PosterCustomMoment moment, PosterSpec spec)
    {
        if (!Guid.TryParse(moment.ItemId, out var id) || _libraryManager.GetItemById(id) is not BaseItem item)
        {
            return null;
        }

        if (item is Series series)
        {
            // A series picked for a cover gives a random moment of a random episode.
            return OneMomentEach(Episodes(series), spec with { Moments = 1 }).FirstOrDefault();
        }

        return CantMake(item) is null ? new Clip(item.Path, ChosenStart(moment.Start, Seconds(item), spec.MomentSeconds), IsHdr(item)) : null;
    }

    /// <summary>Where a picked moment starts: where it was put, kept inside the video, or a random moment for one below 0.</summary>
    public static double ChosenStart(double start, double? runtimeSeconds, double momentSeconds)
    {
        if (start < 0 || double.IsNaN(start))
        {
            return PosterMaker.RandomStart(runtimeSeconds, momentSeconds, Random.Shared);
        }

        return Math.Round(runtimeSeconds is > 0 ? Math.Min(start, Math.Max(0, runtimeSeconds.Value - momentSeconds)) : start, 2);
    }

    private IReadOnlyList<BaseItem> Episodes(Series series) => _libraryManager.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { BaseItemKind.Episode },
        AncestorIds = new[] { series.Id },
        Recursive = true,
        IsVirtualItem = false
    });

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
            var jobs = CoverTargets(config).Where(_ => covers).Select(l => ((BaseItem)l, CoverKind))
                .Concat(Titles().Where(_ => posters).Select(t => (t, PosterKind)))
                .ToList();
            _progress = new RunProgress { Total = jobs.Count };
            // For the time left: how many will need making (the rest are skipped in no time), and how long each takes.
            int toMake = redo ? jobs.Count : jobs.Count(j => _store.Read(s => s.Posters.GetValueOrDefault(j.Item1.Id)) is null);
            int worked = 0;
            double workedSeconds = 0;
            _logger.LogInformation("Medic Posters: checking {Count} titles and libraries{Redo}", jobs.Count, redo ? ", making every one again" : string.Empty);

            for (int i = 0; i < jobs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (item, kind) = jobs[i];
                double? left = worked == 0 ? null : workedSeconds / worked * Math.Max(1, toMake - worked);
                _progress = new RunProgress { Total = jobs.Count, Done = i, Current = item.Name, SecondsLeft = left };
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
                    var began = DateTime.UtcNow;
                    string? error = await MakeForAsync(item, kind, spec, config, ct).ConfigureAwait(false);
                    worked++;
                    workedSeconds += (DateTime.UtcNow - began).TotalSeconds;
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
    public async Task<string?> MakeForAsync(BaseItem item, string kind, PosterSpec spec, PluginConfiguration config, CancellationToken ct)
    {
        var (clips, error) = kind == CoverKind && item is CollectionFolder library ? CoverClips(library, spec, config) : PosterClips(item, spec, config);
        if (error is null)
        {
            // Moments picked by hand, or fewer videos than moments, change how many there are.
            spec = spec with { Moments = clips.Count };
            string output = _store.PosterPath(item.Id);
            string temp = output + ".new.gif";
            error = await _maker.MakeAsync(clips, TitleFor(item, config), spec, temp, ct).ConfigureAwait(false);
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
        IReadOnlyList<BaseItem> candidates = cover ? CoverTargets(config) : Titles(config);
        BaseItem? item = itemId is { } id && id != Guid.Empty
            ? candidates.FirstOrDefault(c => c.Id == id)
            : candidates.OrderBy(_ => Random.Shared.Next()).FirstOrDefault(c => (cover ? CoverClips((CollectionFolder)c, spec, config) : PosterClips(c, spec, config)).Error is null);
        if (item is null && itemId is { } wanted && wanted != Guid.Empty)
        {
            return (null, cover
                ? "That library is left out under Libraries. Tick it, or pick its moments or a library to take them from."
                : "That title isn't in the chosen libraries.");
        }

        if (item is null)
        {
            return (null, candidates.Count == 0
                ? (cover ? "There are no libraries with videos to choose from." : "There are no films or series in the chosen libraries.")
                : "Nothing in the chosen libraries is a video file this server can read.");
        }

        var (clips, error) = cover ? CoverClips((CollectionFolder)item, spec, config) : PosterClips(item, spec, config);
        LastPreview = null;
        if (error is null)
        {
            spec = spec with { Moments = clips.Count };
            error = await _maker.MakeAsync(clips, TitleFor(item, config), spec, _store.PreviewPath, ct).ConfigureAwait(false);
        }

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
    /// <param name="everywhere">Search every library with videos, not just the chosen ones: for picking a cover's films.</param>
    public IReadOnlyList<BaseItem> FindTitles(string text, int limit, bool everywhere = false) =>
        (everywhere ? TitlesIn(Libraries().Select(l => _libraryManager.GetItemById(l.Id)).OfType<CollectionFolder>()) : Titles())
            .Where(t => !t.IsShortcut && t.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

    /// <summary>The films, series and videos with these ids, for showing the moments picked for a poster or cover.</summary>
    public IReadOnlyList<BaseItem> FindItems(IEnumerable<Guid> ids) =>
        ids.Distinct().Select(id => _libraryManager.GetItemById(id)).OfType<BaseItem>()
            .Where(i => i is Video or Series).ToList();

    /// <summary>A still frame of a film, episode or video, for choosing where a moment starts. Null if it can't be read.</summary>
    public async Task<byte[]?> FrameAsync(Guid itemId, double at, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item is Series || CantMake(item) is not null)
        {
            return null;
        }

        return await _maker.FrameAsync(new Clip(item.Path, ChosenStart(Math.Max(0, at), Seconds(item), 0), IsHdr(item)), 320, ct).ConfigureAwait(false);
    }

    /// <summary>How long a video is in seconds, if known.</summary>
    public static double? Seconds(BaseItem film) => film.RunTimeTicks is > 0 ? film.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : null;

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

    /// <summary>
    /// Puts every film's own poster back. Returns how many films were changed. It carries on to the end even when
    /// the page that asked for it is reloaded or closed, or a proxy gives up waiting, so none are left half done.
    /// </summary>
    public async Task<int> RestoreAllAsync()
    {
        await _running.WaitAsync().ConfigureAwait(false);
        try
        {
            var ids = _store.Read(s => s.Posters.Keys.ToList());
            int count = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                _progress = new RunProgress { Kind = "restore", Total = ids.Count, Done = i, Current = _store.Read(s => s.Posters.GetValueOrDefault(ids[i]))?.Title };
                try
                {
                    if (await RestoreAsync(ids[i], CancellationToken.None).ConfigureAwait(false))
                    {
                        count++;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _logger.LogError(ex, "Medic Posters: couldn't put back the own picture of {Id}", ids[i]);
                }
            }

            _store.Update(s => s.Failures.Clear());
            return count;
        }
        finally
        {
            _progress = null;
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


    private static bool IsHdr(BaseItem film)
    {
        var video = film.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return video is not null && video.VideoRange == VideoRange.HDR;
    }
}

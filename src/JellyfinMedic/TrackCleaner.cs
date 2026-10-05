using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

// ---------- Models ----------

public class TrackInfo
{
    public int Index { get; set; }

    public string Kind { get; set; } = string.Empty;   // audio | subtitle

    public string Language { get; set; } = "und";

    public string Codec { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Channels { get; set; } = string.Empty;

    public bool Forced { get; set; }

    public bool Default { get; set; }

    public bool Keep { get; set; } = true;

    public string KeepReason { get; set; } = string.Empty;

    // A separate subtitle file beside the film (never changed by a remux).
    public bool External { get; set; }

    public string? ExternalPath { get; set; }

    // The language as stored in the file, before any language you've set by hand.
    public string FileLanguage { get; set; } = "und";

    // You've set this track's language by hand; it's written into the file on the next run.
    public bool Overridden { get; set; }

    // Bits per second, when Jellyfin knows it (used to estimate the track's size).
    public long? BitRate { get; set; }
}

public class FilePlan
{
    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Library { get; set; } = string.Empty;

    public List<TrackInfo> Tracks { get; set; } = new();

    // The film or episode's name, for lists.
    public string Title { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public int RemovingAudio { get; set; }

    public int RemovingSubtitles { get; set; }

    // Tracks whose language you've set by hand and that still need writing into the file.
    public int TagsToWrite { get; set; }

    // Rough space freed by the tracks being removed.
    public long SavingBytes { get; set; }

    public bool WouldChange => RemovingAudio + RemovingSubtitles + TagsToWrite > 0;

    public bool HasStrippedCopy { get; set; }

    // File size when the run started; remuxing time tracks size far better than file count.
    public long SizeBytes { get; set; }

    public string? Error { get; set; }
}

public class UndeterminedGroup
{
    public string Kind { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public string Channels { get; set; } = string.Empty;

    // Files that have at least one track in this group (a file counts once, however many it has).
    public int Files { get; set; }

    // Every untagged track in this group, across all files.
    public int Tracks { get; set; }

    public List<string> Examples { get; set; } = new();
}

public class TrackScanStatus
{
    public bool Running { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public TrackScanResult? Result { get; set; }

    public string? Error { get; set; }
}

/// <summary>One track with no language, for the paged list.</summary>
public class UntaggedTrack
{
    public string Title { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long FileBytes { get; set; }

    public int Index { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public string Channels { get; set; } = string.Empty;

    public bool External { get; set; }

    public string? ExternalPath { get; set; }

    // The name Jellyfin will recognise for a separate subtitle file, e.g. "Film (2009).eng.srt".
    public string? SuggestedName { get; set; }

    public long? TrackBytes { get; set; }

    // Language you've set by hand, if any.
    public string? SetLanguage { get; set; }

    public bool WillBeRemoved { get; set; }

    // It's the film's only audio track (inside the file), so it's almost certainly the film's own language.
    public bool OnlyAudioTrack { get; set; }

    // The film looks like anime (genre or an "anime" folder), where untagged audio is often Japanese.
    public bool Anime { get; set; }
}

public class PagedList<T>
{
    public int Total { get; set; }

    public int Page { get; set; }

    public int Pages { get; set; }

    public List<T> Items { get; set; } = new();
}

public class TrackScanResult
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public int FilesScanned { get; set; }

    public int FilesWouldChange { get; set; }

    public int AudioToRemove { get; set; }

    public int SubtitlesToRemove { get; set; }

    public List<UndeterminedGroup> Undetermined { get; set; } = new();

    public List<FilePlan> Sample { get; set; } = new(); // first N changing files, for preview

    public int UntaggedCount { get; set; }

    // Files have been changed since this scan, so it may be out of date.
    public bool Stale { get; set; }

    // Full lists, served a page at a time (left out of the summary to keep it small).
    [System.Text.Json.Serialization.JsonIgnore]
    public List<FilePlan> Changing { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<UntaggedTrack> Untagged { get; set; } = new();

    public string? Error { get; set; }
}

public class TrackRunProgress
{
    public bool Running { get; set; }

    public bool DryRun { get; set; } = true;

    public int Total { get; set; }

    public int Done { get; set; }

    public int Changed { get; set; }

    public int Failed { get; set; }

    public long BytesSaved { get; set; }

    public string? Current { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    // Size of every file in this run, and of the files finished so far.
    public long BytesTotal { get; set; }

    public long BytesDone { get; set; }

    // When remuxing began (after the file list was built).
    public DateTime? WorkStartedUtc { get; set; }

    // Estimated seconds left, from the rate data has been processed so far. Null until there's enough to go on.
    public int? SecondsLeft { get; set; }

    // Paused by the user (files already started still finish).
    public bool Paused { get; set; }

    // Why the run is holding off right now, e.g. "Paused" or "Waiting for 01:00–07:00". Null while working.
    public string? Waiting { get; set; }

    // Time spent paused or waiting, left out of the time-left estimate.
    public double WaitedSeconds { get; set; }

    public DateTime? WaitingSinceUtc { get; set; }

    public List<string> Recent { get; set; } = new();
}

// ---------- Engine ----------

/// <summary>
/// Removes unwanted audio and subtitle tracks from LOCAL media files by remuxing with FFmpeg's
/// stream copy (-c copy) — no re-encode, so no quality loss and the GPU isn't needed. It never
/// touches IPTV/.strm files, never removes the last audio or subtitle track, always keeps forced
/// subtitles, and keeps undetermined tracks unless told otherwise. By default it writes the
/// stripped copy beside the original and leaves the original in place; a separate step deletes the
/// originals, and only ones Medic successfully replaced.
/// </summary>
public static class TrackCleaner
{
    private static readonly object Sync = new();
    private static readonly TrackRunProgress Progress = new();
    private static CancellationTokenSource? _cancel;
    private const string StrippedSuffix = ".medic-stripped";   // used by Medic 1.0.4–1.0.7; tidied up by TidyEarlierCopies
    private const string OriginalsFolder = ".medic-originals"; // hidden, and ignored by Jellyfin, Sonarr and Radarr

    public static TrackRunProgress CurrentProgress()
    {
        lock (Sync)
        {
            return Clone(Progress);
        }
    }

    // ---------- Scan / preview ----------

    public static TrackScanResult Scan(ILibraryManager library, IEnumerable<LibraryFacts> libraries, PluginConfiguration cfg, int sampleSize = 50, Action<int, int>? progress = null)
    {
        var result = new TrackScanResult();
        var keep = KeepLanguages(cfg);
        var undetermined = new Dictionary<string, UndeterminedGroup>(StringComparer.Ordinal);

        // Gather first, so progress can say "x of y".
        var work = new List<(LibraryFacts Lib, BaseItem Item)>();
        foreach (var lib in libraries.Where(l => !l.IsStreamed))
        {
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            try
            {
                work.AddRange(library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }).Select(i => (lib, i)));
            }
            catch
            {
                // Skip a library that can't be read.
            }
        }

        int done = 0;
        progress?.Invoke(0, work.Count);
        foreach (var (lib, item) in work)
        {
            if (++done % 50 == 0)
            {
                progress?.Invoke(done, work.Count);
            }

            {
                string path = item.Path ?? string.Empty;
                if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path) || IsMedicFile(path))
                {
                    continue;
                }

                var plan = PlanForItem(item, lib.Name, keep, cfg);
                if (plan is null)
                {
                    continue;
                }

                result.FilesScanned++;
                if (plan.WouldChange)
                {
                    result.FilesWouldChange++;
                    result.AudioToRemove += plan.RemovingAudio;
                    result.SubtitlesToRemove += plan.RemovingSubtitles;
                    result.Changing.Add(plan);
                    if (result.Sample.Count < sampleSize)
                    {
                        result.Sample.Add(plan);
                    }
                }

                bool onlyAudio = plan.Tracks.Count(x => x.Kind == "audio" && !x.External) == 1;
                bool anime = path.Contains("/anime/", StringComparison.OrdinalIgnoreCase)
                    || (SettingsReader.Get(item, "Genres") is IEnumerable<string> genres && genres.Any(g => g.Equals("Anime", StringComparison.OrdinalIgnoreCase)));
                foreach (var t in plan.Tracks.Where(x => x.FileLanguage is "und" or ""))
                {
                    result.Untagged.Add(new UntaggedTrack
                    {
                        OnlyAudioTrack = t.Kind == "audio" && onlyAudio,
                        Anime = anime,
                        Title = plan.Title,
                        FileName = Path.GetFileName(path),
                        Path = path,
                        FileBytes = plan.SizeBytes,
                        Index = t.Index,
                        Kind = t.Kind,
                        Codec = t.Codec,
                        Channels = t.Channels,
                        External = t.External,
                        ExternalPath = t.ExternalPath,
                        SuggestedName = t.External && t.ExternalPath is { } ext
                            ? Path.GetFileNameWithoutExtension(path) + ".eng" + Path.GetExtension(ext)
                            : null,
                        TrackBytes = TrackBytes(t, plan.DurationSeconds),
                        SetLanguage = t.Overridden ? t.Language : null,
                        WillBeRemoved = !t.Keep
                    });
                }

                var countedInThisFile = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in plan.Tracks.Where(x => x.FileLanguage is "und" or ""))
                {
                    string key = t.Kind + "|" + t.Codec + "|" + t.Channels;
                    if (!undetermined.TryGetValue(key, out var g))
                    {
                        g = new UndeterminedGroup { Kind = t.Kind, Codec = t.Codec, Channels = t.Channels };
                        undetermined[key] = g;
                    }

                    g.Tracks++;

                    // A file with several untagged tracks of the same kind counts once, and is listed once.
                    if (!countedInThisFile.Add(key))
                    {
                        continue;
                    }

                    g.Files++;
                    if (g.Examples.Count < 5)
                    {
                        g.Examples.Add(Path.GetFileName(path));
                    }
                }
            }
        }

        progress?.Invoke(work.Count, work.Count);
        result.Undetermined = undetermined.Values.OrderByDescending(g => g.Files).ToList();
        result.UntaggedCount = result.Untagged.Count;
        result.Changing = result.Changing.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
        result.Untagged = result.Untagged.OrderBy(u => u.Title, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Index).ToList();
        return result;
    }

    // ---------- Background scan, so leaving the tab or page doesn't lose it ----------

    private static readonly object ScanSync = new();
    private static bool _scanRunning;
    private static int _scanDone;
    private static int _scanTotal;
    private static TrackScanResult? _lastScan;
    private static string? _scanError;

    public static TrackScanStatus ScanStatus()
    {
        lock (ScanSync)
        {
            return new TrackScanStatus { Running = _scanRunning, Done = _scanDone, Total = _scanTotal, Result = _lastScan, Error = _scanError };
        }
    }

    /// <summary>Starts a scan in the background. Returns false if one is already running.</summary>
    public static bool StartScan(ILibraryManager library, IEnumerable<LibraryFacts> libraries, PluginConfiguration cfg)
    {
        lock (ScanSync)
        {
            if (_scanRunning)
            {
                return false;
            }

            _scanRunning = true;
            _scanDone = 0;
            _scanTotal = 0;
            _scanError = null;
        }

        var libs = libraries.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                var result = Scan(library, libs, cfg, 50, (done, total) =>
                {
                    lock (ScanSync) { _scanDone = done; _scanTotal = total; }
                });
                lock (ScanSync) { _lastScan = result; }
            }
            catch (Exception ex)
            {
                lock (ScanSync) { _scanError = ex.Message; }
            }
            finally
            {
                lock (ScanSync) { _scanRunning = false; }
            }
        });
        return true;
    }

    /// <summary>Forgets the last scan, e.g. after settings change or files are stripped.</summary>
    public static void ClearScan()
    {
        lock (ScanSync)
        {
            if (!_scanRunning)
            {
                _lastScan = null;
            }
        }
    }

    /// <summary>Works out the keep/remove decision for one item's tracks, or null if it has no media streams.</summary>
    public static FilePlan? PlanForItem(BaseItem item, string libraryName, HashSet<string> keep, PluginConfiguration cfg)
    {
        List<object>? streams = ReadStreams(item);
        if (streams is null || streams.Count == 0)
        {
            return null;
        }

        var plan = new FilePlan
        {
            ItemId = item.Id.ToString(),
            Path = item.Path ?? string.Empty,
            Library = libraryName,
            Title = DisplayTitle(item),
            DurationSeconds = (SettingsReader.Number(item, "RunTimeTicks") ?? 0) / 10_000_000d,
            SizeBytes = SettingsReader.Number(item, "Size") ?? 0
        };

        foreach (var s in streams)
        {
            string type = (SettingsReader.Text(s, "Type") ?? string.Empty).ToLowerInvariant();
            if (type is not ("audio" or "subtitle"))
            {
                continue;
            }

            string language = (SettingsReader.Text(s, "Language") ?? "und").Trim().ToLowerInvariant();
            bool external = SettingsReader.Bool(s, "IsExternal") ?? false;
            var track = new TrackInfo
            {
                Index = (int)(SettingsReader.Number(s, "Index") ?? -1),
                Kind = type,
                Language = language,
                FileLanguage = language,
                Codec = (SettingsReader.Text(s, "Codec") ?? string.Empty).ToLowerInvariant(),
                Title = SettingsReader.Text(s, "Title") ?? string.Empty,
                Channels = SettingsReader.Number(s, "Channels") is { } ch ? ch + "ch" : string.Empty,
                Forced = SettingsReader.Bool(s, "IsForced") ?? false,
                Default = SettingsReader.Bool(s, "IsDefault") ?? false,
                External = external,
                ExternalPath = external ? SettingsReader.Text(s, "Path") : null,
                BitRate = SettingsReader.Number(s, "BitRate")
            };

            // A language you've set by hand counts as the track's language from now on.
            if (!external && TrackLanguages.Get(plan.Path, track.Index) is { } chosen)
            {
                track.Language = chosen;
                track.Overridden = true;
            }

            plan.Tracks.Add(track);
        }

        ApplyKeepRules(plan, keep, cfg);
        plan.RemovingAudio = plan.Tracks.Count(t => t.Kind == "audio" && !t.Keep);
        plan.RemovingSubtitles = plan.Tracks.Count(t => t.Kind == "subtitle" && !t.Keep);
        plan.TagsToWrite = plan.Tracks.Count(t => t.Overridden && t.Keep && !string.Equals(t.FileLanguage, t.Language, StringComparison.OrdinalIgnoreCase));
        plan.SavingBytes = plan.Tracks.Where(t => !t.Keep).Sum(t => TrackBytes(t, plan.DurationSeconds) ?? 0);
        plan.HasStrippedCopy = File.Exists(OriginalPath(plan.Path)); // already cleaned once; its original is kept
        return plan;
    }

    /// <summary>
    /// The safety-first keep logic:
    ///  - keep chosen languages and forced subtitles
    ///  - untagged audio is kept unless "remove all untagged tracks" is on
    ///  - untagged subtitles are kept unless either untagged option is on; with "remove untagged
    ///    subtitles", the first untagged subtitle stays in films with no subtitle in your languages
    ///  - never remove the last audio track
    ///  - never leave a film whose audio is in a language you don't keep without subtitles
    ///    (an untagged "only subtitle" may go only if you allow it and the audio isn't known to be foreign)
    /// </summary>
    private static void ApplyKeepRules(FilePlan plan, HashSet<string> keep, PluginConfiguration cfg)
    {
        bool removeAllUntagged = cfg.TracksRemoveUndetermined;
        bool removeUntaggedSubs = cfg.TracksRemoveUntaggedSubtitles;

        foreach (var t in plan.Tracks)
        {
            bool untagged = IsUntagged(t);
            if (t.External)
            {
                t.Keep = true; t.KeepReason = "separate subtitle file (never changed)";
            }
            else if (InLanguage(t, keep))
            {
                t.Keep = true; t.KeepReason = "chosen language";
            }
            else if (t.Kind == "subtitle" && t.Forced)
            {
                t.Keep = true; t.KeepReason = "forced subtitles";
            }
            else if (untagged && t.Kind == "audio" && !removeAllUntagged)
            {
                t.Keep = true; t.KeepReason = removeUntaggedSubs ? "untagged audio (always kept)" : "undetermined (kept by default)";
            }
            else if (untagged && t.Kind == "subtitle" && !removeAllUntagged && !removeUntaggedSubs)
            {
                t.Keep = true; t.KeepReason = "undetermined (kept by default)";
            }
            else
            {
                t.Keep = false; t.KeepReason = untagged ? "untagged" : "other language";
            }
        }

        // Never leave a file with no audio: if nothing audio is kept, keep them all (the native-only / anime case).
        var audio = plan.Tracks.Where(t => t.Kind == "audio").ToList();
        if (audio.Count > 0 && audio.All(t => !t.Keep))
        {
            foreach (var t in audio) { t.Keep = true; t.KeepReason = "only audio in the file"; }
        }

        var subs = plan.Tracks.Where(t => t.Kind == "subtitle").ToList();

        // An untagged "only subtitle" may go when the user allows it.
        bool onlySubtitleMayGo = cfg.TracksAllowRemovingOnlySubtitle && subs.Count == 1 && IsUntagged(subs[0]);

        // Keep the first untagged subtitle when nothing in your languages is kept: on most discs the
        // first subtitle track is the film's own language.
        if (removeUntaggedSubs && cfg.TracksKeepFirstUntaggedSubtitle && !onlySubtitleMayGo && !subs.Any(t => t.Keep && InLanguage(t, keep)))
        {
            var first = subs.Where(IsUntagged).OrderBy(t => t.Index).FirstOrDefault();
            if (first is not null && !first.Keep)
            {
                first.Keep = true; first.KeepReason = "first untagged subtitle (usually the film's own language)";
            }
        }

        // Never leave a foreign-language film with no subtitles: if no audio is in the keep list and
        // all subtitles would go, keep the subtitles so it stays watchable.
        bool keptAudioInLanguage = audio.Any(t => t.Keep && InLanguage(t, keep));
        bool audioKnownForeign = !keptAudioInLanguage && audio.Any(t => t.Keep && !IsUntagged(t));
        if (!keptAudioInLanguage && subs.Count > 0 && subs.All(t => !t.Keep))
        {
            // When the audio is untagged too, Medic can't tell the film is foreign. In that case it goes
            // with the untagged-subtitle choices above (keep-first is the safeguard) instead of keeping everything.
            bool untaggedChoice = !audioKnownForeign
                && (onlySubtitleMayGo || (removeUntaggedSubs && subs.All(IsUntagged)));
            if (!untaggedChoice)
            {
                foreach (var t in subs) { t.Keep = true; t.KeepReason = "only subtitles, audio not in your language"; }
            }
        }
    }

    private static bool IsUntagged(TrackInfo t) => t.Language is "und" or "";

    /// <summary>"removed 2 audio and 13 subtitle tracks, labelled 1 track", in plain words.</summary>
    private static string Summary(FilePlan plan)
    {
        var parts = new List<string>();
        if (plan.RemovingAudio > 0) parts.Add($"removed {plan.RemovingAudio} audio track{(plan.RemovingAudio == 1 ? string.Empty : "s")}");
        if (plan.RemovingSubtitles > 0) parts.Add($"removed {plan.RemovingSubtitles} subtitle track{(plan.RemovingSubtitles == 1 ? string.Empty : "s")}");
        if (plan.TagsToWrite > 0) parts.Add($"set the language on {plan.TagsToWrite} track{(plan.TagsToWrite == 1 ? string.Empty : "s")}");
        return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts);
    }

    /// <summary>Writes a language you've set by hand into the new file, plus a readable title if the track has none.</summary>
    private static void AddLanguageTags(List<string> tags, TrackInfo t, string outStream)
    {
        if (!t.Overridden)
        {
            return;
        }

        tags.Add("-metadata:s:" + outStream);
        tags.Add("language=" + t.Language);
        if (string.IsNullOrWhiteSpace(t.Title))
        {
            tags.Add("-metadata:s:" + outStream);
            tags.Add("title=" + SuggestedTitle(t));
        }
    }

    /// <summary>A track title players show nicely, e.g. "English 5.1" or "English".</summary>
    private static string SuggestedTitle(TrackInfo t)
    {
        string name = LanguageName(t.Language);
        if (t.Kind != "audio")
        {
            return name;
        }

        return t.Channels switch
        {
            "8ch" => name + " 7.1",
            "6ch" => name + " 5.1",
            "2ch" => name + " Stereo",
            "1ch" => name + " Mono",
            _ => name
        };
    }

    public static string LanguageName(string code) => code switch
    {
        "eng" or "en" => "English",
        "jpn" or "ja" => "Japanese",
        "fre" or "fra" or "fr" => "French",
        "ger" or "deu" or "de" => "German",
        "spa" or "es" => "Spanish",
        "ita" or "it" => "Italian",
        "kor" or "ko" => "Korean",
        "chi" or "zho" or "zh" => "Chinese",
        "por" or "pt" => "Portuguese",
        "rus" or "ru" => "Russian",
        "dut" or "nld" or "nl" => "Dutch",
        "swe" or "sv" => "Swedish",
        "nor" or "no" => "Norwegian",
        "dan" or "da" => "Danish",
        "fin" or "fi" => "Finnish",
        "pol" or "pl" => "Polish",
        "hin" or "hi" => "Hindi",
        _ => code
    };

    private static long? TrackBytes(TrackInfo t, double seconds) =>
        t.BitRate is { } bits && bits > 0 && seconds > 0 ? (long)(bits / 8d * seconds) : null;

    private static string DisplayTitle(BaseItem item)
    {
        string? series = SettingsReader.Text(item, "SeriesName");
        long? season = SettingsReader.Number(item, "ParentIndexNumber");
        long? episode = SettingsReader.Number(item, "IndexNumber");
        if (!string.IsNullOrWhiteSpace(series) && season is not null && episode is not null)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} S{1:00}E{2:00}", series, season, episode);
        }

        long? year = SettingsReader.Number(item, "ProductionYear");
        return year is { } y ? $"{item.Name} ({y})" : item.Name ?? System.IO.Path.GetFileNameWithoutExtension(item.Path ?? string.Empty);
    }

    // ---------- Paged lists from the last scan ----------

    public static PagedList<UntaggedTrack> UntaggedPage(int page, int size, string kind, string? search)
    {
        lock (ScanSync)
        {
            var all = (_lastScan?.Untagged ?? new List<UntaggedTrack>())
                .Where(u => kind is "audio" or "subtitle" ? u.Kind == kind : true)
                .Where(u => string.IsNullOrWhiteSpace(search) || u.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || u.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Page(all, page, size);
        }
    }

    public static PagedList<FilePlan> ChangesPage(int page, int size, string? search)
    {
        lock (ScanSync)
        {
            var all = (_lastScan?.Changing ?? new List<FilePlan>())
                .Where(p => string.IsNullOrWhiteSpace(search) || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Page(all, page, size);
        }
    }

    private static PagedList<T> Page<T>(List<T> all, int page, int size)
    {
        size = Math.Clamp(size, 5, 100);
        int pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)size));
        page = Math.Clamp(page, 1, pages);
        return new PagedList<T> { Total = all.Count, Page = page, Pages = pages, Items = all.Skip((page - 1) * size).Take(size).ToList() };
    }

    // ---------- Setting a language by hand ----------

    /// <summary>
    /// Sets (or clears, with an empty language) the language of a track inside a film. It's written into
    /// the file the next time tracks are stripped. Only tracks from the last scan can be changed.
    /// </summary>
    public static (bool Ok, string Message) SetLanguage(string path, int index, string language)
    {
        lock (ScanSync)
        {
            var row = _lastScan?.Untagged.FirstOrDefault(u => u.Path == path && u.Index == index && !u.External);
            if (row is null)
            {
                return (false, "That track isn't in the last scan. Scan again, then try.");
            }

            string code = (language ?? string.Empty).Trim().ToLowerInvariant();
            TrackLanguages.Set(path, index, code.Length == 0 ? null : code);
            row.SetLanguage = code.Length == 0 ? null : code;
            return (true, code.Length == 0
                ? "Cleared."
                : $"Set to {LanguageName(code)}. It's written into the file the next time you press Strip tracks for real.");
        }
    }

    /// <summary>
    /// Sets the language of many tracks inside films at once, for everything matching the filter
    /// (page 0) or one page. By default only audio that is the film's only audio track, and never anime.
    /// </summary>
    public static (int Count, string Message) SetLanguageBulk(string language, string kind, string? search, bool onlyAudioTrack, bool skipAnime, int page, int size)
    {
        string code = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (code.Length is < 2 or > 3 || !code.All(char.IsLetter))
        {
            return (0, "Choose a language first.");
        }

        List<UntaggedTrack> rows;
        lock (ScanSync)
        {
            var all = (_lastScan?.Untagged ?? new List<UntaggedTrack>())
                .Where(u => kind is "audio" or "subtitle" ? u.Kind == kind : true)
                .Where(u => string.IsNullOrWhiteSpace(search) || u.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || u.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            rows = page > 0 ? Page(all, page, size).Items : all;
        }

        var chosen = rows
            .Where(u => !u.External)
            .Where(u => !onlyAudioTrack || u.Kind != "audio" || u.OnlyAudioTrack)
            .Where(u => !skipAnime || !u.Anime)
            .ToList();

        TrackLanguages.SetMany(chosen.Select(u => (u.Path, u.Index)), code);
        lock (ScanSync)
        {
            foreach (var u in chosen)
            {
                u.SetLanguage = code;
            }
        }

        int skipped = rows.Count - chosen.Count;
        return (chosen.Count, $"Set {chosen.Count:N0} track{(chosen.Count == 1 ? string.Empty : "s")} to {LanguageName(code)}."
            + (skipped > 0 ? $" Left {skipped:N0} alone (separate files, anime, or films with more than one audio track)." : string.Empty)
            + " They're written into the files the next time you press Strip tracks for real.");
    }

    /// <summary>
    /// Renames a separate subtitle file so Jellyfin knows its language, e.g. "Film (2009).srt" to
    /// "Film (2009).eng.srt". Only files from the last scan, and only text subtitle formats.
    /// </summary>
    public static (bool Ok, string Message, string? NewName) RenameSubtitle(string path, string language)
    {
        lock (ScanSync)
        {
            var row = _lastScan?.Untagged.FirstOrDefault(u => u.External && u.ExternalPath == path);
            if (row is null || string.IsNullOrEmpty(row.ExternalPath))
            {
                return (false, "That subtitle file isn't in the last scan. Scan again, then try.", null);
            }

            string ext = System.IO.Path.GetExtension(row.ExternalPath).ToLowerInvariant();
            if (ext is not (".srt" or ".ass" or ".ssa" or ".vtt"))
            {
                return (false, "Only .srt, .ass, .ssa and .vtt files can be renamed here.", null);
            }

            string code = (language ?? "eng").Trim().ToLowerInvariant();
            if (code.Length is < 2 or > 3 || !code.All(char.IsLetter))
            {
                return (false, "Choose a language first.", null);
            }

            string folder = System.IO.Path.GetDirectoryName(row.ExternalPath) ?? string.Empty;
            string target = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(row.Path) + "." + code + ext);
            if (File.Exists(target))
            {
                return (false, $"There's already a file called {System.IO.Path.GetFileName(target)}.", null);
            }

            try
            {
                File.Move(row.ExternalPath, target);
            }
            catch (Exception ex)
            {
                return (false, "Couldn't rename it: " + ex.Message, null);
            }

            row.ExternalPath = target;
            row.SetLanguage = code;
            return (true, $"Renamed to {System.IO.Path.GetFileName(target)}. Jellyfin picks it up at the next library scan.", System.IO.Path.GetFileName(target));
        }
    }

    private static bool InLanguage(TrackInfo t, HashSet<string> keep) =>
        keep.Contains(t.Language) || keep.Contains(ThreeToTwo(t.Language));

    // ---------- Run ----------

    public static async Task RunAsync(ILibraryManager library, IEnumerable<LibraryFacts> libraries, string ffmpeg, PluginConfiguration cfg, bool dryRun)
    {
        lock (Sync)
        {
            if (Progress.Running)
            {
                return;
            }

            Progress.Running = true;
            Progress.DryRun = dryRun;
            Progress.Total = 0;
            Progress.Done = 0;
            Progress.Changed = 0;
            Progress.Failed = 0;
            Progress.BytesSaved = 0;
            Progress.Current = null;
            Progress.StartedUtc = DateTime.UtcNow;
            Progress.FinishedUtc = null;
            Progress.BytesTotal = 0;
            Progress.BytesDone = 0;
            Progress.WorkStartedUtc = null;
            Progress.Paused = false;
            Progress.Waiting = null;
            Progress.WaitedSeconds = 0;
            Progress.WaitingSinceUtc = null;
            _paused = false;
            Progress.Recent.Clear();
            _cancel = new CancellationTokenSource();
        }

        var ct = _cancel!.Token;
        var keep = KeepLanguages(cfg);

        try
        {
            var plans = new List<FilePlan>();
            foreach (var lib in libraries.Where(l => !l.IsStreamed))
            {
                if (!Guid.TryParse(lib.Id, out var id))
                {
                    continue;
                }

                var items = library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false });
                foreach (var item in items)
                {
                    string path = item.Path ?? string.Empty;
                    if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path) || IsMedicFile(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    var plan = PlanForItem(item, lib.Name, keep, cfg);
                    if (plan is { WouldChange: true })
                    {
                        plans.Add(plan);
                    }
                }
            }

            foreach (var plan in plans)
            {
                try { plan.SizeBytes = new FileInfo(plan.Path).Length; } catch { plan.SizeBytes = 0; }
            }

            lock (Sync)
            {
                Progress.Total = plans.Count;
                Progress.BytesTotal = plans.Sum(p => p.SizeBytes);
                Progress.WorkStartedUtc = DateTime.UtcNow;
            }

            int concurrency = Math.Clamp(cfg.TracksConcurrentFiles, 1, 4);
            int threads = Math.Clamp(cfg.TracksFfmpegThreads, 0, 16);
            using var limiter = new SemaphoreSlim(concurrency);

            var tasks = plans.Select(async plan =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!dryRun)
                    {
                        await WaitUntilAllowedAsync(cfg, ct).ConfigureAwait(false);
                    }

                    await ProcessOne(plan, ffmpeg, threads, dryRun, cfg.TracksReplaceInPlace, ct).ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user; leave progress as it stands.
        }
        catch (Exception ex)
        {
            Note("Error: " + ex.Message);
        }
        finally
        {
            lock (Sync)
            {
                Progress.Running = false;
                Progress.Current = null;
                Progress.FinishedUtc = DateTime.UtcNow;
            }

            // Files have changed, so the last scan may be out of date. It's kept so the lists stay visible.
            if (!dryRun)
            {
                long saved;
                lock (Sync) { saved = Progress.BytesSaved; }
                WeeklyLedger.AddFreed("Track cleanup", saved);

                lock (ScanSync)
                {
                    if (_lastScan is not null)
                    {
                        _lastScan.Stale = true;
                    }
                }
            }
        }
    }

    private static async Task ProcessOne(FilePlan plan, string ffmpeg, int threads, bool dryRun, bool replaceInPlace, CancellationToken ct)
    {
        lock (Sync) { Progress.Current = Path.GetFileName(plan.Path); }

        if (dryRun)
        {
            lock (Sync) { Progress.Done++; Progress.Changed++; }
            Note($"Would change {plan.Title}: {Summary(plan)}");
            return;
        }

        string temp = plan.Path + ".medic-tmp" + Path.GetExtension(plan.Path);
        try
        {
            // Map only the kept streams: all video, plus the audio/subtitle tracks we keep, by type-relative index.
            var args = new List<string> { "-nostdin", "-y", "-loglevel", "error", "-i", plan.Path, "-map", "0:v?", "-map_metadata", "0", "-map_chapters", "0" };

            // Separate subtitle files aren't in the container, so they're left out of the numbering.
            int audioN = 0, subN = 0, outAudio = 0, outSub = 0;
            var tags = new List<string>();
            foreach (var t in plan.Tracks.Where(x => !x.External).OrderBy(x => x.Index))
            {
                if (t.Kind == "audio")
                {
                    if (t.Keep)
                    {
                        args.Add("-map"); args.Add($"0:a:{audioN}?");
                        AddLanguageTags(tags, t, $"a:{outAudio}");
                        outAudio++;
                    }

                    audioN++;
                }
                else if (t.Kind == "subtitle")
                {
                    if (t.Keep)
                    {
                        args.Add("-map"); args.Add($"0:s:{subN}?");
                        AddLanguageTags(tags, t, $"s:{outSub}");
                        outSub++;
                    }

                    subN++;
                }
            }

            args.AddRange(tags);

            args.Add("-c"); args.Add("copy");
            if (threads > 0) { args.Add("-threads"); args.Add(threads.ToString(CultureInfo.InvariantCulture)); }
            args.Add(temp);

            long before = Size(plan.Path);
            int exit = await RunFfmpeg(ffmpeg, args, ct).ConfigureAwait(false);

            // Verify the new file is real and sane before touching the original.
            if (exit != 0 || !File.Exists(temp) || Size(temp) < before / 3)
            {
                SafeDelete(temp);
                lock (Sync) { Progress.Failed++; Progress.Done++; Progress.BytesDone += plan.SizeBytes; }
                Note($"Skipped {plan.Title}: the new copy didn't come out right, so the original was left alone");
                return;
            }

            long saved = Math.Max(0, before - Size(temp));

            if (replaceInPlace)
            {
                // Replace the original only after the new file is verified.
                string backup = plan.Path + ".medic-orig";
                File.Move(plan.Path, backup, overwrite: true);
                File.Move(temp, plan.Path, overwrite: true);
                SafeDelete(backup);
            }
            else
            {
                // Safe default: move the original into the hidden .medic-originals folder beside it, then put
                // the cleaned file in its place under the same name. The library keeps one file per title, and
                // Jellyfin keeps the same item, artwork and watched status. If a kept original is already there
                // (this file was cleaned before), that one is the true original, so it's left alone.
                string kept = OriginalPath(plan.Path);
                if (File.Exists(kept))
                {
                    File.Move(temp, plan.Path, overwrite: true);
                }
                else
                {
                    EnsureOriginalsFolder(plan.Path);
                    File.Move(plan.Path, kept);
                    File.Move(temp, plan.Path);
                }
            }

            lock (Sync) { Progress.Changed++; Progress.Done++; Progress.BytesSaved += saved; Progress.BytesDone += plan.SizeBytes; }
            Note($"Done {plan.Title}: {Summary(plan)}");
        }
        catch (OperationCanceledException)
        {
            SafeDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(temp);
            lock (Sync) { Progress.Failed++; Progress.Done++; Progress.BytesDone += plan.SizeBytes; }
            Note($"Problem with {plan.Title}: {ex.Message}");
        }
    }

    // ---------- Delete kept originals (only ones Medic kept) ----------

    public static (int Deleted, long Freed) DeleteOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries)
    {
        int deleted = 0;
        long freed = 0;
        foreach (var dir in LibraryFolders(library, libraries))
        {
            string folder = Path.Combine(dir, OriginalsFolder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var kept in SafeFiles(folder))
            {
                if (Path.GetFileName(kept) == ".ignore")
                {
                    continue;
                }

                // Only delete an original whose cleaned version is still in place.
                if (!File.Exists(Path.Combine(dir, Path.GetFileName(kept))))
                {
                    continue;
                }

                try
                {
                    long size = Size(kept);
                    File.Delete(kept);
                    freed += size;
                    deleted++;
                }
                catch
                {
                    // Skip anything locked.
                }
            }

            RemoveIfEmpty(folder);
        }

        return (deleted, freed);
    }

    // ---------- Tidy up copies left by Medic 1.0.4–1.0.7 ----------

    /// <summary>
    /// Earlier versions left "Name.medic-stripped.mkv" beside the original, and could then strip that copy
    /// again ("Name.medic-stripped.medic-stripped.mkv"). This puts each title back to one file: the first
    /// stripped copy takes the original's name, the original moves to .medic-originals, and copies of
    /// copies are deleted. Nothing is deleted unless a good file remains under the original name.
    /// </summary>
    public static (int Titles, int CopiesDeleted, long Freed) TidyEarlierCopies(ILibraryManager library, IEnumerable<LibraryFacts> libraries, bool deleteOriginals = false)
    {
        int titles = 0, deletedCopies = 0;
        long freed = 0;
        foreach (var dir in LibraryFolders(library, libraries))
        {
            var copies = SafeFiles(dir).Where(f => Path.GetFileName(f).Contains(StrippedSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var group in copies.GroupBy(f => OriginalNameFor(f), StringComparer.Ordinal))
            {
                try
                {
                    string original = group.Key;
                    // Shallowest copy first: "Name.medic-stripped.mkv" before "Name.medic-stripped.medic-stripped.mkv".
                    var ordered = group.OrderBy(f => CountSuffix(f)).ThenBy(f => f.Length).ToList();
                    string best = ordered[0];

                    string keptOriginal = OriginalPath(original);
                    if (File.Exists(original))
                    {
                        if (deleteOriginals || File.Exists(keptOriginal))
                        {
                            // Either you asked to keep only the cleaned file, or a true original is already
                            // kept and the file in place is an older cleaned copy.
                            freed += Size(original);
                            File.Delete(original);
                            deletedCopies++;
                        }
                        else
                        {
                            EnsureOriginalsFolder(original);
                            File.Move(original, keptOriginal);
                        }
                    }

                    File.Move(best, original);
                    titles++;

                    if (deleteOriginals && File.Exists(keptOriginal))
                    {
                        freed += Size(keptOriginal);
                        File.Delete(keptOriginal);
                        RemoveIfEmpty(Path.GetDirectoryName(keptOriginal)!);
                    }

                    foreach (var extra in ordered.Skip(1))
                    {
                        freed += Size(extra);
                        File.Delete(extra);
                        deletedCopies++;
                    }
                }
                catch
                {
                    // Leave anything locked or odd exactly as it is.
                }
            }

            // Temp files from interrupted runs.
            foreach (var tmp in SafeFiles(dir).Where(f => Path.GetFileName(f).Contains(".medic-tmp", StringComparison.OrdinalIgnoreCase)))
            {
                try { freed += Size(tmp); File.Delete(tmp); } catch { /* skip */ }
            }
        }

        return (titles, deletedCopies, freed);
    }

    /// <summary>How many originals Medic is keeping in .medic-originals folders, and their total size.</summary>
    public static (int Files, long Bytes) KeptOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries)
    {
        int files = 0;
        long bytes = 0;
        foreach (var dir in LibraryFolders(library, libraries))
        {
            string folder = Path.Combine(dir, OriginalsFolder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var f in SafeFiles(folder).Where(f => Path.GetFileName(f) != ".ignore"))
            {
                files++;
                bytes += Size(f);
            }
        }

        return (files, bytes);
    }

    private static string OriginalNameFor(string copy)
    {
        string dir = Path.GetDirectoryName(copy) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(copy);
        while (name.EndsWith(StrippedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^StrippedSuffix.Length];
        }

        return Path.Combine(dir, name + Path.GetExtension(copy));
    }

    private static int CountSuffix(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int n = 0, i = 0;
        while ((i = name.IndexOf(StrippedSuffix, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            n++;
            i += StrippedSuffix.Length;
        }

        return n;
    }

    /// <summary>Every folder that holds a local file in a non-streamed library.</summary>
    private static HashSet<string> LibraryFolders(ILibraryManager library, IEnumerable<LibraryFacts> libraries)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in libraries.Where(l => !l.IsStreamed))
        {
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            try
            {
                foreach (var item in library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }))
                {
                    string path = item.Path ?? string.Empty;
                    if (path.Length > 0 && LooksLocal(path) && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                    {
                        string? dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !dir.EndsWith(OriginalsFolder, StringComparison.Ordinal))
                        {
                            folders.Add(dir);
                        }
                    }
                }
            }
            catch
            {
                // Skip a library that can't be read.
            }
        }

        return folders;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
    }

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            var left = Directory.GetFileSystemEntries(folder);
            if (left.All(e => Path.GetFileName(e) == ".ignore"))
            {
                foreach (var e in left) { File.Delete(e); }
                Directory.Delete(folder);
            }
        }
        catch
        {
            // Leave it.
        }
    }

    public static void Stop()
    {
        try { _cancel?.Cancel(); } catch { /* already done */ }
    }

    // ---------- Pause, time window, and holding off while people watch ----------

    private static volatile bool _paused;

    /// <summary>How many people are playing something right now. Set by the controller when a run starts.</summary>
    public static Func<int>? WatchingCount { get; set; }

    /// <summary>Pauses after the files already in progress finish. Resume carries on where it left off.</summary>
    public static void Pause()
    {
        _paused = true;
        lock (Sync) { Progress.Paused = Progress.Running; }
    }

    public static void Resume()
    {
        _paused = false;
        lock (Sync) { Progress.Paused = false; }
    }

    private static async Task WaitUntilAllowedAsync(PluginConfiguration startCfg, CancellationToken ct)
    {
        bool waited = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Read settings live, so changing the window or the watching option applies mid-run.
            var cfg = Plugin.Instance?.Configuration ?? startCfg;
            string? reason = WaitReason(cfg);
            if (reason is null)
            {
                break;
            }

            lock (Sync)
            {
                Progress.Waiting = reason;
                Progress.WaitingSinceUtc ??= DateTime.UtcNow;
            }

            waited = true;
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }

        if (waited)
        {
            lock (Sync)
            {
                if (Progress.WaitingSinceUtc is { } since)
                {
                    Progress.WaitedSeconds += (DateTime.UtcNow - since).TotalSeconds;
                }

                Progress.WaitingSinceUtc = null;
                Progress.Waiting = null;
            }
        }
    }

    private static string? WaitReason(PluginConfiguration cfg)
    {
        if (_paused)
        {
            return "Paused";
        }

        if (cfg.TracksWindowEnabled && !InWindow(DateTime.Now.Hour, cfg.TracksWindowStartHour, cfg.TracksWindowEndHour))
        {
            return string.Format(CultureInfo.InvariantCulture, "Waiting for {0:00}:00–{1:00}:00", cfg.TracksWindowStartHour, cfg.TracksWindowEndHour);
        }

        if (cfg.TracksPauseWhileWatching)
        {
            int watching = 0;
            try { watching = WatchingCount?.Invoke() ?? 0; } catch { /* treat as nobody */ }
            if (watching > 0)
            {
                return watching == 1 ? "Waiting: someone is watching" : $"Waiting: {watching} people are watching";
            }
        }

        return null;
    }

    private static bool InWindow(int hour, int start, int end)
    {
        start = Math.Clamp(start, 0, 23);
        end = Math.Clamp(end, 0, 23);
        if (start == end)
        {
            return true;
        }

        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    // ---------- Helpers ----------

    private static HashSet<string> KeepLanguages(PluginConfiguration cfg)
    {
        var set = (cfg.TracksKeepLanguages ?? "eng")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0)
        {
            set.Add("eng");
        }

        // Accept both 2- and 3-letter codes for the common ones.
        foreach (var two in set.Where(s => s.Length == 2).ToList())
        {
            set.Add(TwoToThree(two));
        }

        return set;
    }

    private static List<object>? ReadStreams(BaseItem item)
    {
        try
        {
            var method = item.GetType().GetMethod("GetMediaStreams", Type.EmptyTypes);
            if (method?.Invoke(item, null) is System.Collections.IEnumerable list)
            {
                return list.Cast<object>().ToList();
            }
        }
        catch
        {
            // No streams available for this item.
        }

        return null;
    }

    private static async Task<int> RunFfmpeg(string ffmpeg, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAny(err, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        return p.ExitCode;
    }

    private static string StrippedPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + StrippedSuffix + Path.GetExtension(path));

    /// <summary>Where the untouched original of a file is kept: a hidden folder beside it.</summary>
    private static string OriginalPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, OriginalsFolder, Path.GetFileName(path));

    /// <summary>Files Medic must never treat as library media: its kept originals, temp files and old-style copies.</summary>
    private static bool IsMedicFile(string path) =>
        path.Contains(Path.DirectorySeparatorChar + OriginalsFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains("/" + OriginalsFolder + "/", StringComparison.Ordinal)
        || path.Contains(StrippedSuffix, StringComparison.OrdinalIgnoreCase)
        || path.Contains(".medic-tmp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".medic-orig", StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes the originals folder, with a .ignore file so Jellyfin never adds what's inside it to the library.</summary>
    private static string EnsureOriginalsFolder(string mediaPath)
    {
        string folder = Path.Combine(Path.GetDirectoryName(mediaPath) ?? string.Empty, OriginalsFolder);
        Directory.CreateDirectory(folder);
        string ignore = Path.Combine(folder, ".ignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, string.Empty);
        }

        return folder;
    }

    private static bool LooksLocal(string path) =>
        path.StartsWith('/') || (path.Length > 2 && path[1] == ':');

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static void Note(string line)
    {
        lock (Sync)
        {
            Progress.Recent.Insert(0, $"{DateTime.Now:HH:mm:ss} {line}");
            if (Progress.Recent.Count > 30)
            {
                Progress.Recent.RemoveAt(Progress.Recent.Count - 1);
            }
        }
    }

    // Time left = time so far × data left ÷ data done. Waits for 20 seconds and one finished file so the
    // first estimate isn't wild; dry runs are near-instant, so they get none.
    private static int? EstimateSecondsLeft(TrackRunProgress p)
    {
        if (!p.Running || p.DryRun || p.Waiting is not null || p.WorkStartedUtc is not { } start || p.BytesDone <= 0 || p.BytesTotal <= p.BytesDone)
        {
            return null;
        }

        // Only count time spent working, not time paused or waiting.
        double elapsed = (DateTime.UtcNow - start).TotalSeconds - p.WaitedSeconds;
        if (elapsed < 20)
        {
            return null;
        }

        double left = elapsed * (p.BytesTotal - p.BytesDone) / p.BytesDone;
        return left > int.MaxValue ? int.MaxValue : (int)Math.Round(left);
    }

    private static TrackRunProgress Clone(TrackRunProgress p) => new()
    {
        Running = p.Running, DryRun = p.DryRun, Total = p.Total, Done = p.Done, Changed = p.Changed,
        Failed = p.Failed, BytesSaved = p.BytesSaved, Current = p.Current, StartedUtc = p.StartedUtc,
        BytesTotal = p.BytesTotal, BytesDone = p.BytesDone, WorkStartedUtc = p.WorkStartedUtc, SecondsLeft = EstimateSecondsLeft(p),
        Paused = p.Paused, Waiting = p.Waiting, WaitedSeconds = p.WaitedSeconds, WaitingSinceUtc = p.WaitingSinceUtc,
        FinishedUtc = p.FinishedUtc, Recent = p.Recent.ToList()
    };

    private static string ThreeToTwo(string code) => code switch
    {
        "eng" => "en", "jpn" => "ja", "fre" or "fra" => "fr", "ger" or "deu" => "de", "spa" => "es",
        "ita" => "it", "dut" or "nld" => "nl", "rus" => "ru", "por" => "pt", "chi" or "zho" => "zh",
        "kor" => "ko", "swe" => "sv", "dan" => "da", "nor" => "no", "fin" => "fi", "pol" => "pl", _ => code
    };

    private static string TwoToThree(string code) => code switch
    {
        "en" => "eng", "ja" => "jpn", "fr" => "fre", "de" => "ger", "es" => "spa", "it" => "ita",
        "nl" => "dut", "ru" => "rus", "pt" => "por", "zh" => "chi", "ko" => "kor", "sv" => "swe",
        "da" => "dan", "no" => "nor", "fi" => "fin", "pl" => "pol", _ => code
    };
}

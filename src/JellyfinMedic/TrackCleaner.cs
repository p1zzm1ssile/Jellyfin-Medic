using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

    // Left alone on purpose (out of space, linked, changed meanwhile...): not failures.
    public int Skipped { get; set; }

    public long BytesSaved { get; set; }

    // Size of the originals this run moved into .medic-originals: space still held until they're deleted.
    public long BytesKept { get; set; }

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

/// <summary>A title "Tidy copies" left as it was: its file name, and why its earlier copy didn't check out.</summary>
public sealed record TidyLeft(string Name, string Reason);

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
    private static long _bytesFreed; // space freed by this run right away (not held in kept originals)
    private static readonly SpaceReservations Writing = new(); // bytes the files being cleaned right now may still write
    private static readonly object SpaceSync = new(); // the record of cleaned files and the changes file

    /// <summary>Where each changed file is recorded (one JSON line per file), set by the controller. Null records nothing.</summary>
    public static string? ChangesFile { get; set; }

    private const string CleanedRecord = ".medic-cleaned.json"; // in .medic-originals: what Medic made of each file
    private static bool _ownerNoted; // a folder that couldn't get its parent's owner was noted this run
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
        var exclude = ExcludeList(cfg.TracksExclude);
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
                if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path) || IsMedicFile(path) || IsExcluded(path, exclude))
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
    ///  - an extra (a commentary, audio description, isolated score or karaoke track, going by its title)
    ///    never counts as the film's audio: if only extras would stay, the film's own tracks stay too
    ///  - never leave a film whose audio is in a language you don't keep without subtitles
    ///    (an untagged "only subtitle" may go only if you allow it and the audio isn't known to be foreign)
    /// </summary>
    internal static void ApplyKeepRules(FilePlan plan, HashSet<string> keep, PluginConfiguration cfg)
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
                t.Keep = true; t.KeepReason = IsExtra(t) ? "chosen language (a commentary or other extra)" : "chosen language";
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

        // An extra in your language isn't the film's audio: if only extras would stay, keep the film's own
        // tracks too, the way the guard above does when nothing is in your languages.
        var main = audio.Where(t => !IsExtra(t)).ToList();
        if (main.Count > 0 && main.All(t => !t.Keep))
        {
            foreach (var t in main) { t.Keep = true; t.KeepReason = "the film's own audio (only extras are in your languages)"; }
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

        // Never leave a foreign-language film with no subtitles: if none of the film's own audio is in the
        // keep list and all subtitles would go, keep the subtitles so it stays watchable. Extras don't count:
        // an English commentary doesn't make a Japanese film watchable without subtitles.
        var filmAudio = main.Count > 0 ? main : audio;
        bool keptAudioInLanguage = filmAudio.Any(t => t.Keep && InLanguage(t, keep));
        bool audioKnownForeign = !keptAudioInLanguage && filmAudio.Any(t => t.Keep && !IsUntagged(t));
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

    // Titles that mark an audio track as something other than the film's own soundtrack. "described"
    // catches "Audio Described", which neither "description" nor "descriptive" does.
    private static readonly string[] ExtraAudioWords = { "commentary", "description", "descriptive", "described", "isolated", "karaoke" };

    private static bool IsExtra(TrackInfo t) =>
        t.Kind == "audio" && ExtraAudioWords.Any(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase));

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

            if (_lastScan!.Stale)
            {
                return (false, StaleMessage);
            }

            string code = string.Empty;
            if (!string.IsNullOrWhiteSpace(language))
            {
                if (LanguageCode(language) is not { } valid)
                {
                    return (false, "Choose a language first.");
                }

                code = valid;
            }

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
        if (LanguageCode(language) is not { } code)
        {
            return (0, "Choose a language first.");
        }

        List<UntaggedTrack> rows;
        lock (ScanSync)
        {
            if (_lastScan?.Stale == true)
            {
                return (0, StaleMessage);
            }

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

            if (LanguageCode(language) is not { } code)
            {
                return (false, "Choose a language first.", null);
            }

            // Keep any flags Jellyfin reads from the name, so "Film.forced.srt" becomes "Film.eng.forced.srt".
            string flags = string.Concat(System.IO.Path.GetFileNameWithoutExtension(row.ExternalPath)
                .Split('.')
                .Skip(1)
                .Where(part => SubtitleFlags.Contains(part))
                .Select(part => "." + part.ToLowerInvariant()));
            string folder = System.IO.Path.GetDirectoryName(row.ExternalPath) ?? string.Empty;
            string target = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(row.Path) + "." + code + flags + ext);
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

    // Tracks were stripped since the scan, so its track numbers may no longer match the files.
    private const string StaleMessage = "Tracks have been stripped since the last scan, so its track numbers may be out of date. Scan again, then set the language.";

    // Words in a subtitle file's name that Jellyfin reads as flags rather than a language.
    private static readonly HashSet<string> SubtitleFlags = new(StringComparer.OrdinalIgnoreCase) { "forced", "foreign", "default", "sdh", "cc", "hi" };

    /// <summary>A 2 or 3 letter language code, as the 3 letter B form files use (e.g. "en" → "eng", "ell" → "gre"), or null if it isn't one.</summary>
    private static string? LanguageCode(string? language)
    {
        string code = (language ?? string.Empty).Trim().ToLowerInvariant();
        return code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z') ? LanguageCodes.FileCode(code) : null;
    }

    private static bool InLanguage(TrackInfo t, HashSet<string> keep) => keep.Contains(LanguageCodes.Canonical(t.Language));

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
            Progress.Skipped = 0;
            Progress.BytesSaved = 0;
            Progress.BytesKept = 0;
            _bytesFreed = 0;
            Writing.Reset();
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
            _ownerNoted = false;
            _cancel = new CancellationTokenSource();
        }

        var ct = _cancel!.Token;
        var keep = KeepLanguages(cfg);
        var exclude = ExcludeList(cfg.TracksExclude);

        // cfg is the live settings object that a save changes in place, so reading this for each file
        // would let a save during the run switch keep or replace for the files not started yet.
        bool replaceInPlace = cfg.TracksReplaceInPlace;

        try
        {
            // Hands the request back now: the controller doesn't wait for the run, and without this its request
            // would last through planning, which goes through every file in every library, and through a whole
            // dry run. Running is already set, so a second start is refused meanwhile.
            await Task.Yield();

            // Jellyfin's track list can be older than the file, so every file is probed again right before its remux.
            string? ffprobe = dryRun ? null : FfprobePath(ffmpeg);
            if (!dryRun && ffprobe is null)
            {
                Note("Error: FFprobe wasn't found next to FFmpeg, so no file was changed.");
                return;
            }

            if (!dryRun)
            {
                RecoverInterruptedSwaps(LibraryFolders(library, libraries, ct), ct);
            }

            var plans = new List<(FilePlan Plan, BaseItem Item)>();
            foreach (var lib in libraries.Where(l => !l.IsStreamed))
            {
                ct.ThrowIfCancellationRequested();
                if (!Guid.TryParse(lib.Id, out var id))
                {
                    continue;
                }

                var items = library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false });
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = item.Path ?? string.Empty;
                    if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path) || IsMedicFile(path) || IsExcluded(path, exclude) || !File.Exists(path))
                    {
                        continue;
                    }

                    var plan = PlanForItem(item, lib.Name, keep, cfg);
                    if (plan is { WouldChange: true })
                    {
                        plans.Add((plan, item));
                    }
                }
            }

            foreach (var (plan, _) in plans)
            {
                ct.ThrowIfCancellationRequested();
                try { plan.SizeBytes = new FileInfo(plan.Path).Length; } catch { plan.SizeBytes = 0; }
            }

            lock (Sync)
            {
                Progress.Total = plans.Count;
                Progress.BytesTotal = plans.Sum(p => p.Plan.SizeBytes);
                Progress.WorkStartedUtc = DateTime.UtcNow;
            }

            int concurrency = Math.Clamp(cfg.TracksConcurrentFiles, 1, 4);
            int threads = Math.Clamp(cfg.TracksFfmpegThreads, 0, 16);
            using var limiter = new SemaphoreSlim(concurrency);

            var tasks = plans.Select(async p =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!dryRun)
                    {
                        await WaitUntilAllowedAsync(cfg, ct).ConfigureAwait(false);
                    }

                    await ProcessOne(p.Plan, p.Item, ffmpeg, ffprobe, threads, dryRun, replaceInPlace, ct).ConfigureAwait(false);
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
                // Only space actually freed now: when originals are kept, it's counted when they're deleted.
                long freed;
                lock (Sync) { freed = _bytesFreed; }
                WeeklyLedger.AddFreed("Track cleanup", freed);

                lock (ScanSync)
                {
                    if (_lastScan is not null)
                    {
                        _lastScan.Stale = true;
                    }
                }

                KeptOriginalsWalk.Clear();
            }
        }
    }

    private static async Task ProcessOne(FilePlan plan, BaseItem item, string ffmpeg, string? ffprobe, int threads, bool dryRun, bool replaceInPlace, CancellationToken ct)
    {
        lock (Sync) { Progress.Current = Path.GetFileName(plan.Path); }

        if (dryRun)
        {
            lock (Sync) { Progress.Done++; Progress.Changed++; }
            Note($"Would change {plan.Title}: {Summary(plan)}");
            return;
        }

        // The plan comes from the tracks Jellyfin read at its last scan. If the file has changed since
        // (cleaned before, or replaced by Sonarr or Radarr), mapping by those numbers keeps the wrong tracks.
        ProbedFile? source = ffprobe is null ? null : await ProbeFile(ffprobe, plan.Path, readEnd: false, ct).ConfigureAwait(false);
        if (source is null)
        {
            NotChanged(plan, Unchanged.Unreadable, "FFprobe couldn't read the file, so it was left alone");
            return;
        }

        if (DescribeMismatch(plan, source) is { } mismatch)
        {
            item.ChangedExternally();
            NotChanged(plan, Unchanged.StalePlan, $"Jellyfin's list of its tracks is out of date ({mismatch}). Jellyfin has been asked to read the file again; run track cleanup again once it has.");
            return;
        }

        // Windows has no stat to count hard links with, so only symlinks are checked there.
        var facts = await FileFacts(plan.Path, ct).ConfigureAwait(false);
        if (LinkSkipReason(facts, IsSymlink(plan.Path), countLinks: !OperatingSystem.IsWindows()) is { } why)
        {
            NotChanged(plan, Unchanged.Linked, why);
            return;
        }

        // The cleaned copy is written beside the original: it needs about the original's size free, plus a margin,
        // on top of what other files being cleaned right now are still writing, and the run leaves a floor free
        // for everything else on the drive. Checked and reserved in one step, so two files can't both pass.
        long size = Size(plan.Path);
        if (!Writing.TryReserve(size, () => DriveSpace(plan.Path), out long free, out long total, out long needed))
        {
            NotChanged(plan, Unchanged.NoSpace, $"its drive has {Gb(free)} free and the new copy needs about {Gb(needed)}, keeping {Gb(SpaceFloor(total))} free for everything else, so it was left alone");
            return;
        }

        // To tell whether Sonarr or Radarr replaces the file while it's being cleaned.
        var original = (Length: size, Written: File.GetLastWriteTimeUtc(plan.Path));

        string temp = TempPath(plan.Path);
        try
        {
            // Map only the kept streams: all video, plus the audio/subtitle tracks we keep, by type-relative index.
            // Attachments ("0:t?") are the fonts styled subtitles need in MKV files; without them those subtitles render in the wrong font.
            // -xerror stops at the first read or write error instead of finishing a shorter file.
            var args = new List<string> { "-nostdin", "-y", "-loglevel", "error", "-xerror", "-i", plan.Path, "-map", "0:v?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0" };

            // Separate subtitle files aren't in the container, so they're left out of the numbering.
            int audioN = 0, subN = 0, outAudio = 0, outSub = 0;
            var tags = new List<string>();
            foreach (var t in plan.Tracks.Where(x => !x.External).OrderBy(x => x.Index))
            {
                if (t.Kind == "audio")
                {
                    if (t.Keep)
                    {
                        args.Add("-map"); args.Add($"0:a:{audioN}");
                        AddLanguageTags(tags, t, $"a:{outAudio}");
                        outAudio++;
                    }

                    audioN++;
                }
                else if (t.Kind == "subtitle")
                {
                    if (t.Keep)
                    {
                        args.Add("-map"); args.Add($"0:s:{subN}");
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
            var (exit, errors) = await RunFfmpeg(ffmpeg, args, temp, ct).ConfigureAwait(false);

            // Verify the new file is real and sound before touching the original.
            string? problem = exit != 0 ? "FFmpeg failed" + (errors.Length > 0 ? ": " + errors : string.Empty)
                : !File.Exists(temp) ? "FFmpeg wrote no file"
                : CheckOutput(source, await ProbeFile(ffprobe!, temp, readEnd: true, ct).ConfigureAwait(false), outAudio, outSub, before, Size(temp));
            if (problem is not null)
            {
                SafeDelete(temp);
                NotChanged(plan, Unchanged.BadCopy, $"the new copy didn't come out right ({problem}), so the original was left alone");
                return;
            }

            long saved = Math.Max(0, before - Size(temp));
            long freedNow = 0;
            long keptNow = 0;
            string? keptAs = null;
            FlushToDisk(temp);
            await KeepOwnership(plan.Path, temp, facts, ct).ConfigureAwait(false);

            // If Sonarr or Radarr replaced the file meanwhile, this copy is of the old one: keep the new file.
            if (Size(plan.Path) != original.Length || File.GetLastWriteTimeUtc(plan.Path) != original.Written)
            {
                SafeDelete(temp);
                item.ChangedExternally();
                NotChanged(plan, Unchanged.ReplacedMeanwhile, "it changed while it was being cleaned (replaced by Sonarr or Radarr?), so the new file was kept as it is");
                return;
            }

            if (replaceInPlace)
            {
                // One rename replaces the original, so there is never a moment without a file at its path.
                File.Move(temp, plan.Path, overwrite: true);
                freedNow = saved;
            }
            else
            {
                (freedNow, keptNow, keptAs) = await SwapKeepingOriginal(plan.Path, temp, plan.Title, before, saved, ct).ConfigureAwait(false);
            }

            // Languages set by hand are in the file now, and its track numbers have changed.
            TrackLanguages.ClearFile(plan.Path);

            // Have Jellyfin read the new file, so its track list (and the next plan) matches it.
            item.ChangedExternally();

            lock (Sync) { Progress.Changed++; Progress.Done++; Progress.BytesSaved += saved; Progress.BytesKept += keptNow; _bytesFreed += freedNow; Progress.BytesDone += plan.SizeBytes; }
            Note($"Done {plan.Title}: {Summary(plan)}");
            RecordChange(new TrackChange(DateTime.UtcNow, plan.Path, Summary(plan), before, before - saved, keptAs));
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
        finally
        {
            Writing.Release(size);
        }
    }

    /// <summary>
    /// The safe default's swap: the cleaned copy at temp takes the file's place and the file's original is kept in
    /// .medic-originals. Returns the space freed now, the space the kept original holds, and where it's kept.
    /// </summary>
    internal static async Task<(long FreedNow, long KeptNow, string? KeptAs)> SwapKeepingOriginal(string path, string temp, string title, long before, long saved, CancellationToken ct)
    {
        long freedNow = 0;
        long keptNow = 0;
        string? keptAs = null;

        // A kept original whose file Medic recorded, and which isn't that file any more, belongs to an earlier
        // release (Sonarr or Radarr replaced the file between runs). It's moved aside under a dated name, so
        // the current file is kept as this file's original and nothing is lost.
        string kept = OriginalPath(path);
        if (File.Exists(kept) && ReplacedSinceCleaned(path))
        {
            string aside = ReplacedPath(kept, DateTime.Now);
            File.Move(kept, aside);
            Note($"Kept {Path.GetFileName(aside)}: the original of an earlier release of {title}, replaced since it was cleaned");
        }

        // Safe default: move the original into the hidden .medic-originals folder beside it, then put
        // the cleaned file in its place under the same name. The library keeps one file per title, and
        // Jellyfin keeps the same item, artwork and watched status. If a kept original is already there
        // (this file was cleaned before), that one is the true original, so it's left alone.
        if (File.Exists(kept))
        {
            File.Move(temp, path, overwrite: true);
            freedNow = saved; // the earlier cleaned copy is replaced, not kept
            keptAs = kept;
        }
        else
        {
            // Two moves: if Jellyfin stops between them, the next run (or "Tidy copies") puts the original
            // back, as the temp file beside the empty path shows the swap didn't finish.
            await EnsureOriginalsFolder(path, ct).ConfigureAwait(false);
            File.Move(path, kept);
            try
            {
                File.Move(temp, path);
            }
            catch
            {
                File.Move(kept, path); // put the original back
                throw;
            }

            keptNow = before;
            keptAs = kept;
        }

        RecordCleaned(path);

        return (freedNow, keptNow, keptAs);
    }

    /// <summary>Why a run didn't change a file it planned to clean.</summary>
    internal enum Unchanged
    {
        Unreadable,
        StalePlan,
        Linked,
        NoSpace,
        BadCopy,
        ReplacedMeanwhile,
    }

    /// <summary>Counts a file the run didn't change, with the reason in the run's notes.</summary>
    private static void NotChanged(FilePlan plan, Unchanged reason, string why)
    {
        bool failed;
        lock (Sync) { failed = Tally(Progress, reason, plan.SizeBytes); }
        Note(failed ? $"Problem with {plan.Title}: {why}" : $"Skipped {plan.Title}: {why}");
    }

    /// <summary>
    /// Counts a file the run didn't change; true when it failed. A file Medic chose not to touch is left alone, but one
    /// it tried and couldn't clean (the original unreadable, or the new copy not right) failed, so a run where FFmpeg
    /// fails on every file doesn't look like one that had nothing to do.
    /// </summary>
    internal static bool Tally(TrackRunProgress progress, Unchanged reason, long bytes)
    {
        bool failed = reason is Unchanged.Unreadable or Unchanged.BadCopy;
        if (failed) progress.Failed++; else progress.Skipped++;
        progress.Done++;
        progress.BytesDone += bytes;
        return failed;
    }

    /// <summary>
    /// What a run leaves free on each drive for imports, downloads and Jellyfin itself: the larger of 10 GiB and 5% of
    /// the drive (10 GiB when its size is unknown).
    /// </summary>
    internal static long SpaceFloor(long driveTotal) => Math.Max(10L << 30, driveTotal > 0 ? driveTotal / 20 : 0);

    /// <summary>
    /// Whether a file of this size can be cleaned with this much free: its copy, a margin of the larger of 1 GiB and 5%
    /// of the file, what files being cleaned now may still write, and the floor. On success the copy's size is added to
    /// writing, which the caller takes off again when the file is done. Free below 0 means unknown, which passes.
    /// The caller holds the lock that makes the check and the reservation one step.
    /// </summary>
    internal static bool Reserve(long free, long driveTotal, long size, ref long writing, out long needed)
    {
        needed = size + Math.Max(1L << 30, size / 20) + writing;
        if (free >= 0 && free < needed + SpaceFloor(driveTotal))
        {
            return false;
        }

        writing += size;
        return true;
    }

    /// <summary>What the files being cleaned right now may still write, for the space check each file makes before it starts.</summary>
    internal sealed class SpaceReservations
    {
        private readonly object _sync = new();
        private long _bytes;

        internal long Bytes
        {
            get
            {
                lock (_sync)
                {
                    return _bytes;
                }
            }
        }

        /// <summary>Reads the drive's free space and size, checks and reserves the copy's size as <see cref="Reserve"/> does.</summary>
        internal bool TryReserve(long size, Func<(long Free, long Total)> driveSpace, out long free, out long total, out long needed)
        {
            // Read under the lock too: a figure read before another file's reservation doesn't count that file's copy.
            lock (_sync)
            {
                (free, total) = driveSpace();
                return Reserve(free, total, size, ref _bytes, out needed);
            }
        }

        /// <summary>Takes a reservation off again once its file is done.</summary>
        internal void Release(long size)
        {
            lock (_sync)
            {
                _bytes -= size;
            }
        }

        /// <summary>Starts a run from nothing reserved, so a count gone wrong can't hold files back past the run.</summary>
        internal void Reset()
        {
            lock (_sync)
            {
                _bytes = 0;
            }
        }
    }

    /// <summary>Where an earlier release's kept original goes when the file has been replaced since it was cleaned.</summary>
    internal static string ReplacedPath(string kept, DateTime when) =>
        Path.Combine(Path.GetDirectoryName(kept) ?? string.Empty,
            Path.GetFileNameWithoutExtension(kept) + ".replaced-" + when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(kept));

    // The time is kept for older records; only the length tells a replacement (see ReplacedSinceCleaned).
    private sealed record CleanedFile(long? Length, DateTime WrittenUtc);

    private static string CleanedRecordPath(string mediaPath) =>
        Path.Combine(Path.GetDirectoryName(mediaPath) ?? string.Empty, OriginalsFolder, CleanedRecord);

    private static Dictionary<string, CleanedFile> ReadCleaned(string record)
    {
        try
        {
            return File.Exists(record)
                ? JsonSerializer.Deserialize<Dictionary<string, CleanedFile>>(File.ReadAllText(record)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Notes the size and time of the cleaned file Medic just put in place, so a later run can tell it from a replacement.</summary>
    private static void RecordCleaned(string mediaPath)
    {
        string record = CleanedRecordPath(mediaPath);
        lock (SpaceSync)
        {
            try
            {
                var files = ReadCleaned(record);
                files[Path.GetFileName(mediaPath)] = new CleanedFile(Size(mediaPath), File.GetLastWriteTimeUtc(mediaPath));
                JellyfinMedic.Api.ScheduleStorage.WriteText(record, JsonSerializer.Serialize(files));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Note($"Couldn't note what Medic made of {Path.GetFileName(mediaPath)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Whether the file at this path has been replaced since Medic put it in place: its length differs from the record.
    /// The time alone isn't enough, as a copy without -t, a restore or a tag edit that keeps the size changes it while
    /// the file is still Medic's. Without a record of the length (cleaned by 1.0.22 or earlier) a replacement can't be
    /// told apart, so it's taken to be unchanged. Either way the kept original then stays the title's, instead of being
    /// moved aside on a guess and counted as an orphan that "Delete those with no file too" deletes.
    /// </summary>
    private static bool ReplacedSinceCleaned(string mediaPath)
    {
        Dictionary<string, CleanedFile> files;
        lock (SpaceSync)
        {
            files = ReadCleaned(CleanedRecordPath(mediaPath));
        }

        return files.TryGetValue(Path.GetFileName(mediaPath), out var made)
            && made.Length is { } length && length != Size(mediaPath);
    }

    /// <summary>One file a run changed.</summary>
    public sealed record TrackChange(DateTime WhenUtc, string Path, string Removed, long BytesBefore, long BytesAfter, string? KeptOriginal);

    private const long ChangesLimit = 4L << 20;

    /// <summary>Adds a changed file to the record that outlasts the run (the run's own notes are kept in memory only).</summary>
    private static void RecordChange(TrackChange change)
    {
        if (ChangesFile is not { } file)
        {
            return;
        }

        lock (SpaceSync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                // Past the limit, the older half goes, so the record stays small.
                if (File.Exists(file) && new FileInfo(file).Length > ChangesLimit)
                {
                    var lines = File.ReadAllLines(file);
                    JellyfinMedic.Api.ScheduleStorage.WriteText(file, string.Join('\n', lines.Skip(lines.Length / 2)) + "\n");
                }

                File.AppendAllText(file, JsonSerializer.Serialize(change) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Note($"Couldn't record the change to {Path.GetFileName(change.Path)}: {ex.Message}");
            }
        }
    }

    /// <summary>The newest changes recorded, newest first.</summary>
    public static List<TrackChange> RecentChanges(int count)
    {
        if (ChangesFile is not { } file || !File.Exists(file))
        {
            return new();
        }

        lock (SpaceSync)
        {
            var lines = new List<string>();
            try
            {
                lines.AddRange(File.ReadLines(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The lines read before the failure are still listed; the tab shouldn't fail over the rest.
                NoteOnce($"Couldn't read the record of changed files: {ex.Message}");
            }

            var list = new List<TrackChange>();
            foreach (var line in Enumerable.Reverse(lines).Take(count))
            {
                try
                {
                    if (JsonSerializer.Deserialize<TrackChange>(line) is { } c)
                    {
                        list.Add(c);
                    }
                }
                catch (JsonException)
                {
                    // A line cut short by a crash.
                }
            }

            return list;
        }
    }

    /// <summary>
    /// Why the file's links rule out cleaning it, or null when they don't. Facts are null when stat couldn't read them.
    /// Where hard links aren't counted (countLinks false), only a symlink rules a file out.
    /// </summary>
    internal static string? LinkSkipReason((int Links, uint Uid, uint Gid)? facts, bool isSymlink, bool countLinks)
    {
        // Cleaning works on the path, so the link would give way to a full copy and the file it points to would stay as it is.
        if (isSymlink)
        {
            return "it's a symbolic link, so cleaning it would replace the link with a full copy instead of changing the file it points to";
        }

        if (!countLinks)
        {
            return null;
        }

        // A file with more than one link is shared, usually with a torrent client seeding it. The cleaned copy
        // would be a new file beside the shared one, so the drive would fill up instead of gaining space.
        // Without the count there's no telling, so the file is left alone too.
        if (facts is not { } f)
        {
            return "its hard links couldn't be counted with stat, so it was left alone in case a torrent client is seeding it";
        }

        return f.Links > 1 ? $"it has {f.Links} hard links (a torrent client is probably seeding it), so cleaning it would use more space, not less" : null;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // gone or unreadable, so FFmpeg can't read it either and nothing is replaced
        }
    }

    /// <summary>How many hard links the file has and who owns it, or null where that can't be read (Windows, or no stat).</summary>
    internal static Task<(int Links, uint Uid, uint Gid)?> FileFacts(string path, CancellationToken ct) => FileFacts(path, "stat", ct);

    // Which stat to run is a parameter so tests can stand in for BSD's.
    internal static async Task<(int Links, uint Uid, uint Gid)?> FileFacts(string path, string stat, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        // GNU and busybox stat take -c. BSD and macOS stat refuse it and take -f, where the link count is %l.
        // -1 means stat didn't run at all, so there's nothing to try again.
        var (exit, output) = await RunTool(stat, new[] { "-c", "%h %u %g", "--", path }, ct).ConfigureAwait(false);
        if (exit > 0)
        {
            (exit, output) = await RunTool(stat, new[] { "-f", "%l %u %g", "--", path }, ct).ConfigureAwait(false);
        }

        return exit == 0 ? ParseStat(output) : null;
    }

    /// <summary>Reads the "links uid gid" line that both stat formats print, or null when it isn't one.</summary>
    internal static (int Links, uint Uid, uint Gid)? ParseStat(string output)
    {
        // Owners are unsigned, and some go past int's range (nfsnobody is 4294967294).
        var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int links)
            && uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uid)
            && uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint gid)
            ? (links, uid, gid)
            : null;
    }

    /// <summary>
    /// Gives the cleaned copy the original's permissions and, when Jellyfin is allowed to, its owner, so Sonarr or
    /// Radarr running as another user can still replace or delete it. Best effort: the copy is fine without it.
    /// </summary>
    private static async Task KeepOwnership(string original, string copy, (int Links, uint Uid, uint Gid)? facts, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(copy, File.GetUnixFileMode(original));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the default permissions.
        }

        if (facts is { } f)
        {
            await RunTool("chown", new[] { $"{f.Uid}:{f.Gid}", "--", copy }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Free space and size of the drive holding the file, or -1 for each when they can't be read.</summary>
    private static (long Free, long Total) DriveSpace(string path)
    {
        try
        {
            string dir = Path.GetDirectoryName(path) ?? path;
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(dir) ?? dir : dir);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (-1, -1);
        }
    }

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    /// <summary>Runs a small system tool and returns its exit code and output; -1 if it couldn't run or took over 10 seconds.</summary>
    private static async Task<(int Exit, string Output)> RunTool(string tool, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, string.Empty);
            }

            var output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token); // drained so the tool can't stall on a full pipe
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                ct.ThrowIfCancellationRequested();
                return (-1, string.Empty);
            }

            return (p.ExitCode, (await output.ConfigureAwait(false)).Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty); // the tool isn't installed
        }
    }

    // ---------- Delete kept originals (only ones Medic kept) ----------

    /// <summary>
    /// Deletes kept originals whose cleaned file is still in place under the same name. With orphans, also those with
    /// no file of that name any more (Sonarr or Radarr renamed or replaced it). Never one whose swap was cut short.
    /// </summary>
    public static (int Deleted, long Freed) DeleteOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries, bool orphans = false)
    {
        int deleted = 0;
        long freed = 0;
        try
        {
            foreach (var dir in LibraryFolders(library, libraries))
            {
                string folder = Path.Combine(dir, OriginalsFolder);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var kept in SafeFiles(folder).Where(IsKeptOriginal))
                {
                    // A swap cut short leaves the path empty too, and its original is the title's only untouched copy:
                    // the next run (or "Tidy copies") puts it back.
                    string twin = Path.Combine(dir, Path.GetFileName(kept));
                    if (File.Exists(TempPath(twin)) || (!orphans && !File.Exists(twin)))
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
        }
        finally
        {
            KeptOriginalsWalk.Clear();
        }

        return (deleted, freed);
    }

    // ---------- Tidy up copies left by Medic 1.0.4–1.0.7 ----------

    /// <summary>
    /// Earlier versions left "Name.medic-stripped.mkv" beside the original, and could then strip that copy
    /// again ("Name.medic-stripped.medic-stripped.mkv"). This puts each title back to one file: the first
    /// stripped copy takes the original's name, the original moves to .medic-originals, and copies of
    /// copies are deleted. Nothing is deleted unless a good file remains under the original name, and a copy
    /// FFprobe doesn't read like the film leaves its title as it is.
    /// </summary>
    public static async Task<(int Titles, int CopiesDeleted, long Freed, List<TidyLeft> Left)> TidyEarlierCopies(ILibraryManager library, IEnumerable<LibraryFacts> libraries, string ffprobe, bool deleteOriginals = false)
    {
        lock (Sync)
        {
            _ownerNoted = false;
        }

        int titles = 0, deletedCopies = 0;
        var left = new List<TidyLeft>();
        long freed = 0;
        try
        {
            foreach (var dir in LibraryFolders(library, libraries))
            {
                // Only a copy's end is read: the original it's compared with is only read for its header.
                var (t, d, f, l) = await TidyFolder(dir, path => ProbeFile(ffprobe, path, readEnd: Path.GetFileName(path).Contains(StrippedSuffix, StringComparison.OrdinalIgnoreCase), CancellationToken.None), deleteOriginals, CancellationToken.None).ConfigureAwait(false);
                titles += t;
                deletedCopies += d;
                freed += f;
                left.AddRange(l);
            }
        }
        finally
        {
            // Tidying moves originals into .medic-originals, and can delete them.
            KeptOriginalsWalk.Clear();
        }

        return (titles, deletedCopies, freed, left);
    }

    /// <summary>Tidies one folder. probe reads a file with FFprobe; it's passed in so the tests can run without it.</summary>
    internal static async Task<(int Titles, int CopiesDeleted, long Freed, List<TidyLeft> Left)> TidyFolder(string dir, Func<string, Task<ProbedFile?>> probe, bool deleteOriginals, CancellationToken ct)
    {
        int titles = 0, deletedCopies = 0;
        var left = new List<TidyLeft>();
        long freed = 0;
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

                // Versions 1.0.4 to 1.0.7 made these copies with optional maps and checked nothing, so a copy
                // missing its video, audio or end must not take the place of a good file. A kept original is
                // the true one; without either, the copy only has to read.
                string? reference = File.Exists(keptOriginal) ? keptOriginal : File.Exists(original) ? original : null;
                string? problem = CheckLeftover(
                    reference is null ? null : await probe(reference).ConfigureAwait(false),
                    await probe(best).ConfigureAwait(false),
                    reference is null ? 0 : Size(reference),
                    Size(best));
                if (problem is not null)
                {
                    left.Add(new TidyLeft(Path.GetFileName(original), problem));
                    Note($"Left {Path.GetFileName(original)} and its earlier copy as they are: the copy didn't check out ({problem})");
                    continue;
                }

                if (File.Exists(original))
                {
                    if (deleteOriginals || File.Exists(keptOriginal))
                    {
                        // Either you asked to keep only the cleaned file, or a true original is already
                        // kept and the file in place is an older cleaned copy. The copy replaces it in one
                        // rename below, so the title is never left without a file if that fails.
                        freed += Size(original);
                        deletedCopies++;
                    }
                    else
                    {
                        await EnsureOriginalsFolder(original, ct).ConfigureAwait(false);
                        File.Move(original, keptOriginal);
                    }
                }

                File.Move(best, original, overwrite: true);
                titles++;

                // The copy in place is Medic's own, so a later release under this name can be told from it.
                if (!deleteOriginals && File.Exists(keptOriginal))
                {
                    RecordCleaned(original);
                }

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
            catch (Exception ex)
            {
                // Leave anything locked or odd as it is, but say so.
                Note($"Couldn't tidy {Path.GetFileName(group.Key)}: {ex.Message}");
            }
        }

        // Temp files from interrupted runs, once any original they belong to is back in place.
        RecoverInterruptedSwaps(new[] { dir });
        foreach (var tmp in SafeFiles(dir).Where(f => Path.GetFileName(f).Contains(".medic-tmp", StringComparison.OrdinalIgnoreCase)))
        {
            try { freed += Size(tmp); File.Delete(tmp); } catch { /* skip */ }
        }

        return (titles, deletedCopies, freed, left);
    }

    /// <summary>A file in .medic-originals that is a kept original, not Medic's own .ignore or record.</summary>
    private static bool IsKeptOriginal(string path) => !Path.GetFileName(path).StartsWith('.');

    /// <summary>
    /// A kept original with no file of its name in the library folder any more, which "Delete those with no file too"
    /// deletes. One whose swap was cut short (the temp file is still there) isn't: the next run puts it back.
    /// </summary>
    internal static bool IsOrphan(string dir, string kept)
    {
        string twin = Path.Combine(dir, Path.GetFileName(kept));
        return !File.Exists(twin) && !File.Exists(TempPath(twin));
    }

    // Cleared by whatever keeps, moves or deletes originals: a track run, tidying, and deleting them.
    internal static readonly LibraryWalkCache<(int Files, long Bytes, int Orphans, long OrphanBytes)> KeptOriginalsWalk = new(TimeProvider.System);

    /// <summary>How many originals Medic is keeping in .medic-originals folders and their total size, and how many of them are orphans.</summary>
    public static (int Files, long Bytes, int Orphans, long OrphanBytes) KeptOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries, CancellationToken ct = default) =>
        KeptOriginalsWalk.Get(c => CountKeptOriginals(library, libraries, c), ct);

    private static (int Files, long Bytes, int Orphans, long OrphanBytes) CountKeptOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries, CancellationToken ct)
    {
        int files = 0, orphans = 0;
        long bytes = 0, orphanBytes = 0;
        foreach (var dir in LibraryFolders(library, libraries, ct))
        {
            ct.ThrowIfCancellationRequested();
            string folder = Path.Combine(dir, OriginalsFolder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var f in SafeFiles(folder).Where(IsKeptOriginal))
            {
                long size = Size(f);
                files++;
                bytes += size;
                if (IsOrphan(dir, f))
                {
                    orphans++;
                    orphanBytes += size;
                }
            }
        }

        return (files, bytes, orphans, orphanBytes);
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
    private static HashSet<string> LibraryFolders(ILibraryManager library, IEnumerable<LibraryFacts> libraries, CancellationToken ct = default)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in libraries.Where(l => !l.IsStreamed))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            try
            {
                foreach (var item in library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }))
                {
                    ct.ThrowIfCancellationRequested();
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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

    // What Medic itself writes in a .medic-originals folder; with only these left, no original is kept there.
    private static readonly HashSet<string> MedicsOwn = new(StringComparer.Ordinal) { ".ignore", CleanedRecord, CleanedRecord + ".tmp" };

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            var left = Directory.GetFileSystemEntries(folder);
            if (left.All(e => MedicsOwn.Contains(Path.GetFileName(e))))
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

    // Stored in the form tracks are compared in (LanguageCodes.Canonical), so "fre", "fra" and "fr" each keep a
    // French track, whichever form the file uses.
    internal static HashSet<string> KeepLanguages(PluginConfiguration cfg)
    {
        var set = (cfg.TracksKeepLanguages ?? "eng")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(LanguageCodes.Canonical)
            .ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0)
        {
            set.Add("eng");
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

    /// <summary>FFprobe from the same folder as FFmpeg, as Jellyfin ships them together; null if it isn't there.</summary>
    internal static string? FfprobePath(string ffmpeg)
    {
        string path = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty, "ffprobe" + Path.GetExtension(ffmpeg));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// What FFprobe reports about a file: every stream in order (kind is video, audio, subtitle, attachment...) and the
    /// duration when known. EndProblem is why its last packets don't reach that duration, when they were read.
    /// </summary>
    internal sealed record ProbedFile(List<(int Index, string Kind, string Language)> Streams, double? DurationSeconds, string? EndProblem = null)
    {
        public int Count(string kind) => Streams.Count(s => s.Kind == kind);
    }

    /// <summary>
    /// The file's own streams and duration, or null if FFprobe couldn't read it. With readEnd, also where its last
    /// packets stop, for a copy about to take a file's place.
    /// </summary>
    private static async Task<ProbedFile?> ProbeFile(string ffprobe, string path, bool readEnd, CancellationToken ct)
    {
        string? json = await RunFfprobe(ffprobe, ["-v", "error", "-show_entries", "format=start_time,duration:stream=index,codec_type:stream_tags=language", "-of", "json", path], ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        List<(int, string, string)> streams;
        double start;
        double? duration;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            streams = new List<(int, string, string)>();
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                string kind = s.TryGetProperty("codec_type", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                string language = s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var l) ? l.GetString() ?? "und" : "und";
                streams.Add((s.GetProperty("index").GetInt32(), kind, language.Trim().ToLowerInvariant()));
            }

            double? Seconds(string name) => doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty(name, out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
            duration = Seconds("duration") is > 0 and var known ? known : null;
            start = Seconds("start_time") ?? 0;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }

        // A file cut part way keeps its header, so the duration above is still the film's whole length. Without a
        // duration there's no end to look for, and LengthProblem falls back on the file's size.
        string? endProblem = readEnd && duration is { } stated
            ? EndProblem(await RunFfprobe(ffprobe, ["-v", "error", "-read_intervals", EndInterval(start, stated), "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", path], ct).ConfigureAwait(false), start, stated)
            : null;
        return new ProbedFile(streams, duration, endProblem);
    }

    /// <summary>What FFprobe printed, or null if it failed or took over 2 minutes.</summary>
    private static async Task<string?> RunFfprobe(string ffprobe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = p.StandardError.ReadToEndAsync(timeout.Token); // drained so FFprobe can't stall on a full pipe
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested(); // stop pressed; otherwise it timed out
            return null;
        }

        return p.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
    }

    // The last packets are read from this long before the stated end. A seek lands on the keyframe before that
    // point, or on the last data a cut file has, and reading stops at its end of file, well before the window closes.
    private const double EndWindowSeconds = 10;

    /// <summary>FFprobe's -read_intervals for the last packets: from shortly before the stated end to a minute past it.</summary>
    internal static string EndInterval(double start, double duration) =>
        Math.Max(0, start + duration - EndWindowSeconds).ToString("0.###", CultureInfo.InvariantCulture)
        + "%+" + (EndWindowSeconds + 60).ToString(CultureInfo.InvariantCulture);

    /// <summary>When the last packet ends, from FFprobe's "pts_time,duration_time" lines in any stream order; null when none has a timestamp.</summary>
    internal static double? LastPacketEnd(string packets)
    {
        double? last = null;
        foreach (var line in packets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
            {
                continue;
            }

            double length = fields.Length > 1 && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            last = Math.Max(last ?? double.MinValue, pts + length);
        }

        return last;
    }

    /// <summary>
    /// Why a copy's last packets don't reach its stated length, or null when they do. packets is null when FFprobe
    /// couldn't read them; a copy whose end can't be read isn't trusted with a file's place.
    /// </summary>
    internal static string? EndProblem(string? packets, double start, double duration)
    {
        if (packets is null || LastPacketEnd(packets) is not { } last)
        {
            return "FFprobe couldn't read its end";
        }

        return start + duration - last > LengthTolerance(duration) ? $"it stops at {last - start:0} s though it says it lasts {duration:0} s" : null;
    }

    /// <summary>
    /// Why the planned tracks don't describe the file any more, or null when they do: same streams, at the same
    /// positions, of the same kind and language. Separate subtitle files aren't in the container, so they're left out.
    /// </summary>
    private static string? DescribeMismatch(FilePlan plan, ProbedFile probed)
    {
        var file = probed.Streams.Where(s => s.Kind is "audio" or "subtitle").ToList();
        var planned = plan.Tracks.Where(t => !t.External).OrderBy(t => t.Index).ToList();
        int Count(IEnumerable<string> kinds, string kind) => kinds.Count(k => k == kind);
        if (planned.Count != file.Count)
        {
            return $"Jellyfin lists {Count(planned.Select(t => t.Kind), "audio")} audio and {Count(planned.Select(t => t.Kind), "subtitle")} subtitle tracks, "
                + $"the file has {Count(file.Select(t => t.Kind), "audio")} and {Count(file.Select(t => t.Kind), "subtitle")}";
        }

        for (int i = 0; i < planned.Count; i++)
        {
            var (index, kind, language) = file[i];
            if (planned[i].Index != index || planned[i].Kind != kind)
            {
                return $"stream {index} in the file is {kind}, Jellyfin expects {planned[i].Kind} at stream {planned[i].Index}";
            }

            if (!SameLanguage(planned[i].FileLanguage, language))
            {
                return $"{kind} stream {index} is {language} in the file, Jellyfin has {planned[i].FileLanguage}";
            }
        }

        return null;
    }

    /// <summary>Jellyfin stores "ell" for a file tagged "gre", so both 3-letter forms and the 2-letter code count as one.</summary>
    internal static bool SameLanguage(string a, string b)
    {
        static string Norm(string code) => LanguageCodes.Canonical(string.IsNullOrWhiteSpace(code) ? "und" : code);
        return Norm(a) == Norm(b);
    }

    /// <summary>
    /// Why the new file can't replace the original, or null when it's sound: FFprobe reads it, it has the same video
    /// and attachments, exactly the audio and subtitle tracks that were kept, and the same length. When either length
    /// is unknown, a file much smaller than the original fails instead. Its last packets have to reach that length too.
    /// </summary>
    internal static string? CheckOutput(ProbedFile source, ProbedFile? output, int audio, int subtitles, long sourceBytes, long outputBytes)
    {
        if (output is null)
        {
            return "FFprobe couldn't read it";
        }

        foreach (var (kind, expected) in new[] { ("video", source.Count("video")), ("attachment", source.Count("attachment")), ("audio", audio), ("subtitle", subtitles) })
        {
            if (output.Count(kind) != expected)
            {
                return $"it has {output.Count(kind)} {kind} streams instead of {expected}";
            }
        }

        return LengthProblem(source, output, sourceBytes, outputBytes) ?? output.EndProblem;
    }

    /// <summary>
    /// Why an earlier copy can't take the original's place, or null when it can. Lighter than CheckOutput, as the
    /// tracks those versions kept aren't known and they dropped attachments: FFprobe reads it, it has the original's
    /// video, some audio, and the same length, which its last packets reach. Without an original to compare, video,
    /// audio and an end that reaches its own stated length are enough.
    /// </summary>
    internal static string? CheckLeftover(ProbedFile? reference, ProbedFile? leftover, long referenceBytes, long leftoverBytes)
    {
        if (leftover is null)
        {
            return "FFprobe couldn't read it";
        }

        if (reference is null)
        {
            return leftover.Count("video") == 0 ? "it has no video" : leftover.Count("audio") == 0 ? "it has no audio" : leftover.EndProblem;
        }

        if (leftover.Count("video") != reference.Count("video"))
        {
            return $"it has {leftover.Count("video")} video streams instead of {reference.Count("video")}";
        }

        return leftover.Count("audio") == 0 ? "it has no audio" : LengthProblem(reference, leftover, referenceBytes, leftoverBytes) ?? leftover.EndProblem;
    }

    /// <summary>Why the copy isn't as long as the original, or null. When either length is unknown, a copy much smaller than the original fails instead.</summary>
    private static string? LengthProblem(ProbedFile source, ProbedFile copy, long sourceBytes, long copyBytes)
    {
        if (source.DurationSeconds is { } want && copy.DurationSeconds is { } got)
        {
            return Math.Abs(want - got) > LengthTolerance(want) ? $"it lasts {got:0} s instead of {want:0} s" : null;
        }

        return sourceBytes > 0 && copyBytes < sourceBytes / 3 ? "it's much smaller than the original and its length can't be read" : null;
    }

    /// <summary>How far a copy's length may be off, by its header or by its last packets: 2 s, or 1% of a long film.</summary>
    private static double LengthTolerance(double seconds) => Math.Max(2, seconds * 0.01);

    /// <summary>Makes sure the new file is on the disk before it takes the original's place, so a power cut can't leave an empty file.</summary>
    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Flush(flushToDisk: true);
    }

    // A remux that writes nothing for this long is stuck (a hung network drive, for example) and is stopped.
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(10);

    /// <summary>Runs FFmpeg writing to output. Returns its exit code and the end of its error output, for the note when it fails.</summary>
    private static async Task<(int Exit, string Errors)> RunFfmpeg(string ffmpeg, List<string> args, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
        long lastSize = -1;
        var lastGrowth = DateTime.UtcNow;
        try
        {
            while (!p.HasExited)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tick.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await p.WaitForExitAsync(tick.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Still running: check that the new file is growing.
                }

                long size = Size(output);
                if (size != lastSize)
                {
                    lastSize = size;
                    lastGrowth = DateTime.UtcNow;
                }
                else if (!p.HasExited && DateTime.UtcNow - lastGrowth > StallLimit)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return (-1, $"it wrote nothing for {StallLimit.TotalMinutes:0} minutes, so it was stopped");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop pressed: end FFmpeg too, or it carries on writing the file in the background.
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        await Task.WhenAny(err, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        string errors = err.IsCompletedSuccessfully ? err.Result.Trim() : string.Empty;
        if (errors.Length > 300)
        {
            errors = "..." + errors[^300..];
        }

        return (p.ExitCode, errors.Replace('\n', ' ').Replace('\r', ' '));
    }

    /// <summary>Where the untouched original of a file is kept: a hidden folder beside it.</summary>
    private static string OriginalPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, OriginalsFolder, Path.GetFileName(path));

    /// <summary>Where the cleaned copy is written before it takes the original's place.</summary>
    private static string TempPath(string path) => path + ".medic-tmp" + Path.GetExtension(path);

    /// <summary>
    /// Puts originals back where a swap was cut short (Jellyfin stopped between moving the original away and
    /// moving the cleaned copy in): the path is empty, the original is kept, and the temp file is still beside it.
    /// Without the temp file the title was removed on purpose, so it's left alone. Also handles the ".medic-orig"
    /// backups that "replace in place" used before it swapped in one step. Returns how many were put back.
    /// A stop takes effect between folders, never during a move.
    /// </summary>
    private static int RecoverInterruptedSwaps(IEnumerable<string> folders, CancellationToken ct = default)
    {
        int restored = 0;
        foreach (var dir in folders)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = new List<(string From, string To)>();
            string kept = Path.Combine(dir, OriginalsFolder);
            if (Directory.Exists(kept))
            {
                candidates.AddRange(SafeFiles(kept)
                    .Where(IsKeptOriginal)
                    .Select(f => (f, Path.Combine(dir, Path.GetFileName(f))))
                    .Where(c => File.Exists(TempPath(c.Item2))));
            }

            candidates.AddRange(SafeFiles(dir)
                .Where(f => f.EndsWith(".medic-orig", StringComparison.Ordinal))
                .Select(f => (f, f[..^".medic-orig".Length])));

            foreach (var (from, to) in candidates.Where(c => !File.Exists(c.To)))
            {
                try
                {
                    File.Move(from, to);
                    SafeDelete(TempPath(to));
                    restored++;
                    Note($"Put back the original of {Path.GetFileName(to)}: an earlier run stopped while swapping it");
                }
                catch (Exception ex)
                {
                    Note($"Couldn't put back the original of {Path.GetFileName(to)}: {ex.Message}. It's still in {from}");
                }
            }
        }

        return restored;
    }

    /// <summary>Files Medic must never treat as library media: its kept originals, temp files and old-style copies.</summary>
    private static bool IsMedicFile(string path) =>
        path.Contains(Path.DirectorySeparatorChar + OriginalsFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains("/" + OriginalsFolder + "/", StringComparison.Ordinal)
        || path.Contains(StrippedSuffix, StringComparison.OrdinalIgnoreCase)
        || path.Contains(".medic-tmp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".medic-orig", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the originals folder, with a .ignore file so Jellyfin never adds what's inside it to the library. What
    /// it makes gets the permissions and owner of the folder it's in: with Jellyfin's own, Sonarr or Radarr running
    /// as another user couldn't delete the film's folder, and with the setgid bit, files keep the shared group.
    /// </summary>
    internal static async Task<string> EnsureOriginalsFolder(string mediaPath, CancellationToken ct)
    {
        string parent = Path.GetDirectoryName(mediaPath) ?? string.Empty;
        string folder = Path.Combine(parent, OriginalsFolder);
        var made = new List<string>();
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            made.Add(folder);
        }

        string ignore = Path.Combine(folder, ".ignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, string.Empty);
            made.Add(ignore);
        }

        if (made.Count == 0 || OperatingSystem.IsWindows())
        {
            return folder;
        }

        // Owner first: a change of owner can clear the setgid bit.
        if (await FileFacts(parent, ct).ConfigureAwait(false) is { } facts)
        {
            foreach (var path in made)
            {
                if (!await MatchOwner(path, facts.Uid, facts.Gid, ct).ConfigureAwait(false))
                {
                    OwnerNotMatched(folder);
                    break;
                }
            }
        }

        try
        {
            var mode = new DirectoryInfo(parent).UnixFileMode;
            const UnixFileMode NotForFiles = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                | UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;
            foreach (var path in made)
            {
                FileSystemInfo info = path == folder ? new DirectoryInfo(path) : new FileInfo(path);
                var wanted = path == folder ? mode : mode & ~NotForFiles;
                // Skipped when already right: a chmod by someone outside the folder's group drops the setgid bit it inherited.
                if (info.UnixFileMode != wanted)
                {
                    info.UnixFileMode = wanted;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the default permissions.
        }

        return folder;
    }

    /// <summary>Gives the path this owner and group, or only the group where Jellyfin may not give files away; false when neither works.</summary>
    internal static async Task<bool> MatchOwner(string path, uint uid, uint gid, CancellationToken ct)
    {
        var (owner, _) = await RunTool("chown", new[] { $"{uid}:{gid}", "--", path }, ct).ConfigureAwait(false);
        if (owner == 0)
        {
            return true;
        }

        var (group, _) = await RunTool("chgrp", new[] { gid.ToString(CultureInfo.InvariantCulture), "--", path }, ct).ConfigureAwait(false);
        return group == 0;
    }

    // Once per run: Jellyfin running as its own user can't give folders away, so a note for each would bury the others.
    private static void OwnerNotMatched(string folder)
    {
        lock (Sync)
        {
            if (_ownerNoted)
            {
                return;
            }

            _ownerNoted = true;
        }

        Note($"Couldn't give {folder} the owner or group of the folder it's in, so Sonarr or Radarr running as another user may not be able to delete it. Other folders may be the same.");
    }

    /// <summary>The exclusion lines: trimmed, no blanks or repeats, at most 200.</summary>
    public static List<string> ExcludeList(string? text) =>
        (text ?? string.Empty).Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

    // A folder ("/media/anime") or part of a name ("The Office"); slashes match either way round.
    private static bool IsExcluded(string path, List<string> exclude)
    {
        if (exclude.Count == 0)
        {
            return false;
        }

        string p = path.Replace('\\', '/');
        return exclude.Any(e => p.Contains(e.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
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

    // For a note made each time the page looks, not once per run: a second one would push run notes out.
    private static void NoteOnce(string line)
    {
        lock (Sync)
        {
            if (!Progress.Recent.Any(l => l.EndsWith(" " + line, StringComparison.Ordinal)))
            {
                Note(line);
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
        Failed = p.Failed, Skipped = p.Skipped, BytesSaved = p.BytesSaved, BytesKept = p.BytesKept, Current = p.Current, StartedUtc = p.StartedUtc,
        BytesTotal = p.BytesTotal, BytesDone = p.BytesDone, WorkStartedUtc = p.WorkStartedUtc, SecondsLeft = EstimateSecondsLeft(p),
        Paused = p.Paused, Waiting = p.Waiting, WaitedSeconds = p.WaitedSeconds, WaitingSinceUtc = p.WaitingSinceUtc,
        FinishedUtc = p.FinishedUtc, Recent = p.Recent.ToList()
    };
}

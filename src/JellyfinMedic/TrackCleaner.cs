using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

// ---------- Engine ----------

/// <summary>
/// Removes unwanted audio and subtitle tracks from LOCAL media files by remuxing with FFmpeg's
/// stream copy (-c copy) — no re-encode, so no quality loss and the GPU isn't needed. It never
/// touches IPTV/.strm files, never removes the last audio or subtitle track, always keeps forced
/// subtitles, and keeps undetermined tracks unless told otherwise. By default it writes the
/// stripped copy beside the original and leaves the original in place; a separate step deletes the
/// originals, and only ones Medic successfully replaced.
/// </summary>
public static partial class TrackCleaner
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
}

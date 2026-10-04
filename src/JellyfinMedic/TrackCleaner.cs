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
}

public class FilePlan
{
    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Library { get; set; } = string.Empty;

    public List<TrackInfo> Tracks { get; set; } = new();

    public int RemovingAudio { get; set; }

    public int RemovingSubtitles { get; set; }

    public bool WouldChange => RemovingAudio + RemovingSubtitles > 0;

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

public class TrackScanResult
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public int FilesScanned { get; set; }

    public int FilesWouldChange { get; set; }

    public int AudioToRemove { get; set; }

    public int SubtitlesToRemove { get; set; }

    public List<UndeterminedGroup> Undetermined { get; set; } = new();

    public List<FilePlan> Sample { get; set; } = new(); // first N changing files, for preview

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
    private const string StrippedSuffix = ".medic-stripped";

    public static TrackRunProgress CurrentProgress()
    {
        lock (Sync)
        {
            return Clone(Progress);
        }
    }

    // ---------- Scan / preview ----------

    public static TrackScanResult Scan(ILibraryManager library, IEnumerable<LibraryFacts> libraries, PluginConfiguration cfg, int sampleSize = 50)
    {
        var result = new TrackScanResult();
        var keep = KeepLanguages(cfg);
        var undetermined = new Dictionary<string, UndeterminedGroup>(StringComparer.Ordinal);

        foreach (var lib in libraries.Where(l => !l.IsStreamed))
        {
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            List<BaseItem> items;
            try
            {
                items = library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var item in items)
            {
                string path = item.Path ?? string.Empty;
                if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path))
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
                    if (result.Sample.Count < sampleSize)
                    {
                        result.Sample.Add(plan);
                    }
                }

                var countedInThisFile = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in plan.Tracks.Where(IsUntagged))
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

        result.Undetermined = undetermined.Values.OrderByDescending(g => g.Files).ToList();
        return result;
    }

    /// <summary>Works out the keep/remove decision for one item's tracks, or null if it has no media streams.</summary>
    public static FilePlan? PlanForItem(BaseItem item, string libraryName, HashSet<string> keep, PluginConfiguration cfg)
    {
        List<object>? streams = ReadStreams(item);
        if (streams is null || streams.Count == 0)
        {
            return null;
        }

        var plan = new FilePlan { ItemId = item.Id.ToString(), Path = item.Path ?? string.Empty, Library = libraryName };

        foreach (var s in streams)
        {
            string type = (SettingsReader.Text(s, "Type") ?? string.Empty).ToLowerInvariant();
            if (type is not ("audio" or "subtitle"))
            {
                continue;
            }

            plan.Tracks.Add(new TrackInfo
            {
                Index = (int)(SettingsReader.Number(s, "Index") ?? -1),
                Kind = type,
                Language = (SettingsReader.Text(s, "Language") ?? "und").Trim().ToLowerInvariant(),
                Codec = (SettingsReader.Text(s, "Codec") ?? string.Empty).ToLowerInvariant(),
                Title = SettingsReader.Text(s, "Title") ?? string.Empty,
                Channels = SettingsReader.Number(s, "Channels") is { } ch ? ch + "ch" : string.Empty,
                Forced = SettingsReader.Bool(s, "IsForced") ?? false,
                Default = SettingsReader.Bool(s, "IsDefault") ?? false
            });
        }

        ApplyKeepRules(plan, keep, cfg);
        plan.RemovingAudio = plan.Tracks.Count(t => t.Kind == "audio" && !t.Keep);
        plan.RemovingSubtitles = plan.Tracks.Count(t => t.Kind == "subtitle" && !t.Keep);
        plan.HasStrippedCopy = File.Exists(StrippedPath(plan.Path));
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
            if (InLanguage(t, keep))
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
                    if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path) || !File.Exists(path))
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
        }
    }

    private static async Task ProcessOne(FilePlan plan, string ffmpeg, int threads, bool dryRun, bool replaceInPlace, CancellationToken ct)
    {
        lock (Sync) { Progress.Current = Path.GetFileName(plan.Path); }

        if (dryRun)
        {
            lock (Sync) { Progress.Done++; Progress.Changed++; }
            Note($"Would strip {plan.RemovingAudio} audio / {plan.RemovingSubtitles} subtitle track(s) from {Path.GetFileName(plan.Path)}");
            return;
        }

        string temp = plan.Path + ".medic-tmp" + Path.GetExtension(plan.Path);
        try
        {
            // Map only the kept streams: all video, plus the audio/subtitle tracks we keep, by type-relative index.
            var args = new List<string> { "-nostdin", "-y", "-loglevel", "error", "-i", plan.Path, "-map", "0:v?", "-map_metadata", "0", "-map_chapters", "0" };

            int audioN = 0, subN = 0;
            foreach (var t in plan.Tracks.OrderBy(t => t.Index))
            {
                if (t.Kind == "audio")
                {
                    if (t.Keep) { args.Add("-map"); args.Add($"0:a:{audioN}?"); }
                    audioN++;
                }
                else if (t.Kind == "subtitle")
                {
                    if (t.Keep) { args.Add("-map"); args.Add($"0:s:{subN}?"); }
                    subN++;
                }
            }

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
                Note($"Skipped {Path.GetFileName(plan.Path)} (remux failed or output looked wrong)");
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
                // Safe default: keep the original, leave the stripped copy beside it.
                File.Move(temp, StrippedPath(plan.Path), overwrite: true);
            }

            lock (Sync) { Progress.Changed++; Progress.Done++; Progress.BytesSaved += saved; Progress.BytesDone += plan.SizeBytes; }
            Note($"Stripped {Path.GetFileName(plan.Path)} (removed {plan.RemovingAudio} audio / {plan.RemovingSubtitles} subs)");
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
            Note($"Error on {Path.GetFileName(plan.Path)}: {ex.Message}");
        }
    }

    // ---------- Delete originals (only ones we successfully replaced) ----------

    public static (int Deleted, long Freed) DeleteOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries)
    {
        int deleted = 0;
        long freed = 0;
        foreach (var lib in libraries.Where(l => !l.IsStreamed))
        {
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            List<BaseItem> items;
            try
            {
                items = library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var item in items)
            {
                string path = item.Path ?? string.Empty;
                string stripped = StrippedPath(path);
                if (string.IsNullOrEmpty(path) || !File.Exists(stripped) || !File.Exists(path))
                {
                    continue;
                }

                try
                {
                    long size = Size(path);
                    // Replace the original with the stripped copy, keeping the original's name.
                    File.Delete(path);
                    File.Move(stripped, path);
                    freed += size - Size(path);
                    deleted++;
                }
                catch
                {
                    // Skip anything locked.
                }
            }
        }

        return (deleted, freed);
    }

    public static void Stop()
    {
        try { _cancel?.Cancel(); } catch { /* already done */ }
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
        if (!p.Running || p.DryRun || p.WorkStartedUtc is not { } start || p.BytesDone <= 0 || p.BytesTotal <= p.BytesDone)
        {
            return null;
        }

        double elapsed = (DateTime.UtcNow - start).TotalSeconds;
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

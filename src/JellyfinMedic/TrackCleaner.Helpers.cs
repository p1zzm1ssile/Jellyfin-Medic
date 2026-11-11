using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.Entities;

namespace JellyfinMedic.Services;

/// <summary>Track cleanup: probing files, running FFmpeg, checking copies, and small helpers.</summary>
public static partial class TrackCleaner
{
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

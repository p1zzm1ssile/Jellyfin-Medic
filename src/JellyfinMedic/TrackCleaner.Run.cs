using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

/// <summary>Track cleanup: running the cleanup, file by file, and recording what changed.</summary>
public static partial class TrackCleaner
{
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
}

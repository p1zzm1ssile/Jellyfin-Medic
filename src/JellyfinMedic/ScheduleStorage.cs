using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinMedic.Api;

/// <summary>
/// File locations, locking and formatting shared by the controller and the background services.
/// Data lives under plugins/configurations/JellyfinMedic so that replacing the plugin's
/// own folder during an update doesn't delete backups or history.
/// All times are shown on the 24-hour clock.
/// </summary>
public static class ScheduleStorage
{
    public static readonly object SyncRoot = new();

    // Every run of the last week is kept, since the Dashboard's Busy hours counts a week of runs.
    private static readonly TimeSpan RunHistoryWeek = TimeSpan.FromDays(7);

    // A task's newest runs are kept however old, so one that runs once a day or less still has a history.
    internal const int KeptRunsPerTask = 300;

    // Per task, so one that runs every minute can't push every other task's runs out. A week of runs
    // every 5 minutes is 2,016; the rest leaves room for runs started by hand.
    internal const int MaxRunsPerTask = 2100;

    // In all, oldest first, so a week of five tasks that run every 5 minutes still fits. A run takes about
    // 320 bytes, so under 4 MB; only if every run failed with the longest error details would it reach about 55 MB.
    internal const int MaxRunHistory = 12000;

    // Runs that finish this close together are written to disk together.
    private static readonly TimeSpan RunWriteDelay = TimeSpan.FromSeconds(5);

    // Runs kept in memory while the history can't be written (a full disk, a locked file). The oldest go first.
    internal const int MaxPendingRuns = 4000;
    private const int MaxAuditEntries = 1000; // the page shows the newest 200
    private const int MaxBackups = 50;
    private const int MaxErrorDetailsLength = 4000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Set by the plugin when it starts, so problems with these files reach Jellyfin's log.</summary>
    internal static ILogger? Logger { get; set; }

    // ---------- Paths ----------

    public static string DataDir(IApplicationPaths paths) => Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic");

    public static string BackupDir(IApplicationPaths paths) => Path.Combine(DataDir(paths), "Backups");

    public static string AuditLogPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "movement_audit.json");

    public static string ProfilePath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "task_learning_profile.json");

    public static string ManagedPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "managed_schedule.json");

    public static string RunHistoryPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "run_history.json");

    public static string ChoicesPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "task_choices.json");

    // ---------- Per-task choices ----------

    /// <summary>What the owner chose for a task in Preview: let Medic schedule it, keep its schedule, or leave it off.</summary>
    public const string ChoiceMedic = "medic";
    public const string ChoiceKeep = "keep";
    public const string ChoiceOff = "off";

    public static Dictionary<string, string> LoadChoices(IApplicationPaths paths)
    {
        lock (SyncRoot)
        {
            return ReadList<TaskChoice>(ChoicesPath(paths))
                .Where(c => c.Choice is ChoiceMedic or ChoiceKeep or ChoiceOff)
                .GroupBy(c => c.TaskId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Choice, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveChoice(IApplicationPaths paths, string taskId, string choice)
    {
        lock (SyncRoot)
        {
            var list = ReadList<TaskChoice>(ChoicesPath(paths));
            list.RemoveAll(c => SameId(c.TaskId, taskId));
            list.Add(new TaskChoice { TaskId = taskId, Choice = choice });
            WriteJson(ChoicesPath(paths), list);
        }
    }

    // ---------- Reading and writing ----------

    public static List<T> ReadList<T>(string path)
    {
        if (!File.Exists(path))
        {
            return new List<T>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new List<T>();
        }
        catch (JsonException ex)
        {
            SetAside(path, ex);
            return new List<T>();
        }
    }

    /// <summary>
    /// Keeps a file that couldn't be read as name.bad: the next write would otherwise replace it, and everything in
    /// it would be lost. If it can't be moved, the error goes up and nothing writes over it.
    /// </summary>
    public static void SetAside(string path, Exception why)
    {
        string aside = path + ".bad";
        try
        {
            File.Move(path, aside, overwrite: true);
            Logger?.LogWarning(why, "Jellyfin Medic: {File} couldn't be read, so it was kept as {Aside} and a new one started", Path.GetFileName(path), Path.GetFileName(aside));
        }
        catch (FileNotFoundException)
        {
            // Another reader set it aside first.
        }
    }

    public static void WriteJson<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, Indented));

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then swaps it in. A crash or power cut mid-write
    /// leaves the old file whole, instead of a broken one that reads back as empty and loses everything in it.
    /// </summary>
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true); // without this the rename can reach the disk before the data does
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static Dictionary<string, ReliabilityRecord> LoadProfile(IApplicationPaths paths)
    {
        List<ReliabilityRecord> records;
        lock (SyncRoot)
        {
            records = ReadList<ReliabilityRecord>(ProfilePath(paths));
        }

        return records
            .GroupBy(r => r.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, ManagedRun> LoadManaged(IApplicationPaths paths)
    {
        List<ManagedRun> runs;
        lock (SyncRoot)
        {
            runs = ReadList<ManagedRun>(ManagedPath(paths));
        }

        return runs
            .GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public static MovementAudit Audit(string taskName, string previous, string next, string rationale) => new()
    {
        Timestamp = DateTime.UtcNow,
        TaskName = taskName,
        PreviousTime = previous,
        NewTime = next,
        Rationale = rationale
    };

    public static void AppendAudit(IApplicationPaths paths, string taskName, string previous, string next, string rationale) =>
        AppendAudits(paths, new[] { Audit(taskName, previous, next, rationale) });

    /// <summary>Adds entries to the schedule changes log in one write, keeping the newest ones.</summary>
    public static void AppendAudits(IApplicationPaths paths, IReadOnlyCollection<MovementAudit> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        lock (SyncRoot)
        {
            string path = AuditLogPath(paths);
            var logs = ReadList<MovementAudit>(path);
            logs.AddRange(entries);

            if (logs.Count > MaxAuditEntries)
            {
                logs = logs.OrderBy(l => l.Timestamp).Skip(logs.Count - MaxAuditEntries).ToList();
            }

            WriteJson(path, logs);
        }
    }

    /// <summary>Deletes all but the newest schedule backups. <paramref name="keep"/> is never deleted.</summary>
    public static void PruneBackups(IApplicationPaths paths, string? keep = null)
    {
        var dir = new DirectoryInfo(BackupDir(paths));
        if (!dir.Exists)
        {
            return;
        }

        var old = dir.GetFiles("schedule_backup_*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxBackups)
            .Where(f => !string.Equals(f.Name, keep, StringComparison.OrdinalIgnoreCase));
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger?.LogWarning(ex, "Jellyfin Medic: couldn't delete the old schedule backup {File}", file.Name);
            }
        }
    }

    // Finished runs not written to disk yet, by history file. Readers see them through ReadRuns.
    private static readonly Dictionary<string, List<RunRecord>> PendingRuns = new(StringComparer.Ordinal);

    // History files with a write already on its way, so a run that finishes meanwhile joins it.
    private static readonly HashSet<string> FlushScheduled = new(StringComparer.Ordinal);

    // History files whose last write failed, so the failure is logged once and its recovery once.
    private static readonly HashSet<string> FailingRunWrites = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a finished run. It's written a few seconds later, together with any other run that finishes
    /// meanwhile, so a burst of runs (the night's tasks, say) rewrites the history once, not once per run.
    /// </summary>
    public static void AppendRun(IApplicationPaths paths, RunRecord run)
    {
        string path = RunHistoryPath(paths);
        bool schedule;
        lock (SyncRoot)
        {
            if (!PendingRuns.TryGetValue(path, out var pending))
            {
                pending = new List<RunRecord>();
                PendingRuns[path] = pending;
            }

            pending.Add(run);
            if (pending.Count > MaxPendingRuns)
            {
                pending.RemoveRange(0, pending.Count - MaxPendingRuns);
            }

            schedule = FlushScheduled.Add(path);
        }

        if (schedule)
        {
            _ = Task.Delay(RunWriteDelay).ContinueWith(_ => FlushRuns(paths), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Writes the runs still waiting to be written. Also called when the plugin stops. If the write fails they
    /// stay waiting, and the next run's write or the plugin stopping tries them again.
    /// </summary>
    public static void FlushRuns(IApplicationPaths paths)
    {
        string path = RunHistoryPath(paths);
        lock (SyncRoot)
        {
            FlushScheduled.Remove(path);
            if (!PendingRuns.TryGetValue(path, out var pending) || pending.Count == 0)
            {
                return;
            }

            try
            {
                var runs = ReadList<RunRecord>(path);
                runs.AddRange(pending);
                WriteJson(path, Trim(runs, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                if (FailingRunWrites.Add(path))
                {
                    Logger?.LogWarning(ex, "Jellyfin Medic: couldn't save the latest task runs to the run history; they're kept and saved with the next run");
                }

                return;
            }

            PendingRuns.Remove(path);
            if (FailingRunWrites.Remove(path))
            {
                Logger?.LogInformation("Jellyfin Medic: the run history is being saved again");
            }
        }
    }

    /// <summary>The run history, the runs not written to disk yet included.</summary>
    public static List<RunRecord> ReadRuns(IApplicationPaths paths)
    {
        string path = RunHistoryPath(paths);
        lock (SyncRoot)
        {
            var runs = ReadList<RunRecord>(path);
            if (PendingRuns.TryGetValue(path, out var pending))
            {
                runs.AddRange(pending);
            }

            return runs;
        }
    }

    /// <summary>
    /// Keeps of each task its newest KeptRunsPerTask runs and every run of the last week, up to MaxRunsPerTask,
    /// then the newest MaxRunHistory in all. Oldest first.
    /// </summary>
    internal static List<RunRecord> Trim(IEnumerable<RunRecord> runs, DateTime nowUtc)
    {
        var weekAgo = AsUtc(nowUtc) - RunHistoryWeek;
        return runs.GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(r => r.EndUtc)
                .Where((r, i) => i < KeptRunsPerTask || AsUtc(r.EndUtc) >= weekAgo)
                .Take(MaxRunsPerTask))
            .OrderByDescending(r => r.EndUtc)
            .Take(MaxRunHistory)
            .OrderBy(r => r.EndUtc)
            .ToList();
    }

    /// <summary>
    /// Forgets any pending back-off for these tasks, so a later successful run
    /// doesn't overwrite a schedule that was just applied or restored.
    /// </summary>
    public static void ClearBackoffs(IApplicationPaths paths, IEnumerable<string> taskIds)
    {
        lock (SyncRoot)
        {
            string path = ProfilePath(paths);
            if (!File.Exists(path))
            {
                return;
            }

            var ids = new HashSet<string>(taskIds, StringComparer.OrdinalIgnoreCase);
            var records = ReadList<ReliabilityRecord>(path);
            bool changed = false;

            foreach (var record in records.Where(r => r.OriginalTriggers is not null && ids.Contains(r.TaskKey)))
            {
                record.OriginalTriggers = null;
                changed = true;
            }

            if (changed)
            {
                WriteJson(path, records);
            }
        }
    }

    // ---------- Tasks ----------

    /// <summary>
    /// Hidden or disabled tasks (for example Live TV tasks when Live TV isn't set up) are ignored.
    /// </summary>
    public static bool IsVisible(IScheduledTaskWorker worker) =>
        worker.ScheduledTask is not IConfigurableScheduledTask configurable ||
        (!configurable.IsHidden && configurable.IsEnabled);

    public static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static IScheduledTaskWorker? FindWorker(ITaskManager taskManager, string taskId) =>
        taskManager.ScheduledTasks.FirstOrDefault(t => SameId(t.Id.ToString(), taskId));

    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static RunRecord ToRunRecord(IScheduledTaskWorker worker, TaskResult result)
    {
        string? details = result.LongErrorMessage;
        if (details is not null && details.Length > MaxErrorDetailsLength)
        {
            details = details[..MaxErrorDetailsLength] + " …";
        }

        return new RunRecord
        {
            TaskId = worker.Id.ToString(),
            TaskName = worker.Name,
            StartUtc = AsUtc(result.StartTimeUtc),
            EndUtc = AsUtc(result.EndTimeUtc),
            Status = result.Status.ToString(),
            ErrorMessage = result.ErrorMessage,
            ErrorDetails = details
        };
    }

    // Jellyfin's own tasks by key, which is the same in every language (the name is translated).
    private static readonly Dictionary<string, string> CategoryByKey = new(StringComparer.Ordinal)
    {
        ["RefreshLibrary"] = "library", ["RefreshGuide"] = "library", ["RefreshInternetChannels"] = "library",
        ["RefreshChapterImages"] = "analysis", ["RefreshTrickplayImages"] = "analysis", ["MoveTrickplayImages"] = "analysis",
        ["TaskExtractMediaSegments"] = "analysis", ["KeyframeExtraction"] = "analysis", ["AudioNormalization"] = "analysis",
        ["DownloadSubtitles"] = "analysis", ["DownloadLyrics"] = "analysis",
        ["OptimizeDatabaseTask"] = "upkeep", ["DeleteCacheFiles"] = "upkeep", ["CleanLogFiles"] = "upkeep",
        ["PluginUpdates"] = "upkeep", ["DeleteTranscodeFiles"] = "upkeep", ["RefreshPeople"] = "upkeep",
        ["CleanActivityLog"] = "upkeep", ["CleanupUserDataTask"] = "upkeep"
    };

    /// <summary>Colour group on the timeline.</summary>
    public static string Category(IScheduledTaskWorker worker, bool managed)
    {
        if (managed)
        {
            return "monthly";
        }

        if (CategoryByKey.TryGetValue(worker.ScheduledTask.Key, out var category))
        {
            return category;
        }

        // Tasks from plugins, by their (usually English) name.
        string name = worker.Name;
        bool Has(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (Has("Scan Media Library", "Guide", "Channels"))
        {
            return "library";
        }

        if (Has("Chapter", "Trickplay", "Segment", "Keyframe", "Intro", "Audio Normali", "Subtitle", "Lyrics"))
        {
            return "analysis";
        }

        if (Has("Optimize", "Optimise", "Cache", "Log Directory", "Update Plugins", "Transcode", "People", "Activity", "Clean", "user data"))
        {
            return "upkeep";
        }

        return "plugin";
    }

    /// <summary>How long a task usually takes, in minutes, for drawing it and planning around it.</summary>
    public static double TypicalMinutes(IScheduledTaskWorker worker, ReliabilityRecord? rec)
    {
        if (rec is { TotalRuns: > 0 })
        {
            return Math.Max(2, rec.AverageDurationSeconds / 60);
        }

        var last = worker.LastExecutionResult;
        if (last is not null && last.Status == TaskCompletionStatus.Completed)
        {
            return Math.Max(2, (last.EndTimeUtc - last.StartTimeUtc).TotalMinutes);
        }

        return 10;
    }

    // ---------- Formatting (24-hour clock throughout) ----------

    public static string Hhmm(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    public static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;

    public static string ShortDay(DayOfWeek day) => day.ToString()[..3];

    public static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalMinutes < 1)
        {
            return $"{(int)span.TotalSeconds} s";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes} min";
        }

        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalHours < 1)
        {
            return $"{interval.TotalMinutes:0} min";
        }

        if (interval.TotalHours < 48)
        {
            return $"{interval.TotalHours:0.##} hrs";
        }

        return $"{interval.TotalDays:0.#} days";
    }

    public static string FormatManaged(ManagedRun run) =>
        $"First {run.Weekday} of each month at {Hhmm(TimeSpan.FromTicks(run.TimeOfDayTicks))} (run by Medic)";

    /// <summary>
    /// The date of the first given weekday in a month, e.g. the first Thursday of October.
    /// </summary>
    public static DateTime FirstWeekdayOfMonth(int year, int month, DayOfWeek weekday)
    {
        var first = new DateTime(year, month, 1);
        int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset);
    }

    public static string FormatSchedule(IEnumerable<TaskTriggerInfo>? triggers, ManagedRun? managed) =>
        managed is not null ? FormatManaged(managed) : FormatTriggers(triggers);

    public static string FormatTriggers(IEnumerable<TaskTriggerInfo>? triggers)
    {
        var list = triggers?.ToList() ?? new List<TaskTriggerInfo>();
        if (list.Count == 0)
        {
            return "Manual only";
        }

        var parts = new List<string>();
        var handled = new HashSet<TaskTriggerInfo>();

        foreach (var t in list
                     .Where(t => t.Type == TaskTriggerInfoType.DailyTrigger && t.TimeOfDayTicks.HasValue)
                     .OrderBy(t => t.TimeOfDayTicks))
        {
            parts.Add($"Daily at {Hhmm(TimeSpan.FromTicks(t.TimeOfDayTicks.GetValueOrDefault()))}");
            handled.Add(t);
        }

        // Weekly triggers at the same time are shown together: "Mon, Wed, Fri at 03:30".
        foreach (var group in list
                     .Where(t => t.Type == TaskTriggerInfoType.WeeklyTrigger && t.TimeOfDayTicks.HasValue && t.DayOfWeek.HasValue)
                     .GroupBy(t => t.TimeOfDayTicks.GetValueOrDefault())
                     .OrderBy(g => g.Key))
        {
            var days = group
                .Select(t => t.DayOfWeek.GetValueOrDefault())
                .Distinct()
                .OrderBy(MondayFirst)
                .Select(ShortDay);
            parts.Add($"{string.Join(", ", days)} at {Hhmm(TimeSpan.FromTicks(group.Key))}");
            foreach (var t in group)
            {
                handled.Add(t);
            }
        }

        foreach (var t in list.Where(t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks.HasValue))
        {
            parts.Add($"Every {FormatInterval(TimeSpan.FromTicks(t.IntervalTicks.GetValueOrDefault()))}");
            handled.Add(t);
        }

        foreach (var t in list.Where(t => !handled.Contains(t)))
        {
            parts.Add(t.Type.ToString());
        }

        return string.Join(", ", parts);
    }
}

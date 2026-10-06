using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

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

    private const int MaxRunHistory = 2000;
    private const int MaxErrorDetailsLength = 4000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---------- Paths ----------

    public static string DataDir(IApplicationPaths paths) => Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic");

    public static string BackupDir(IApplicationPaths paths) => Path.Combine(DataDir(paths), "Backups");

    public static string AuditLogPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "movement_audit.json");

    public static string ProfilePath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "task_learning_profile.json");

    public static string ManagedPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "managed_schedule.json");

    public static string RunHistoryPath(IApplicationPaths paths) => Path.Combine(DataDir(paths), "run_history.json");

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
        catch (JsonException)
        {
            return new List<T>();
        }
    }

    public static void WriteJson<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, Indented));

    /// <summary>
    /// Writes to a temporary file, then swaps it in. A crash or power cut mid-write leaves the old file
    /// whole, instead of a broken one that reads back as empty and loses everything in it.
    /// </summary>
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
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

    public static void AppendAudit(IApplicationPaths paths, string taskName, string previous, string next, string rationale)
    {
        lock (SyncRoot)
        {
            string path = AuditLogPath(paths);
            var logs = ReadList<MovementAudit>(path);
            logs.Add(new MovementAudit
            {
                Timestamp = DateTime.UtcNow,
                TaskName = taskName,
                PreviousTime = previous,
                NewTime = next,
                Rationale = rationale
            });
            WriteJson(path, logs);
        }
    }

    public static void AppendRun(IApplicationPaths paths, RunRecord run)
    {
        lock (SyncRoot)
        {
            string path = RunHistoryPath(paths);
            var runs = ReadList<RunRecord>(path);
            runs.Add(run);

            // Keep the file a manageable size: drop the oldest runs first.
            if (runs.Count > MaxRunHistory)
            {
                runs = runs.OrderBy(r => r.EndUtc).Skip(runs.Count - MaxRunHistory).ToList();
            }

            WriteJson(path, runs);
        }
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
                record.BackedOffSinceUtc = null;
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

    /// <summary>Colour group on the timeline.</summary>
    public static string Category(IScheduledTaskWorker worker, bool managed)
    {
        if (managed)
        {
            return "monthly";
        }

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

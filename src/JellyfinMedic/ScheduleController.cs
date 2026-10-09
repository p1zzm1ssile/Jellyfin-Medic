using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Services;

namespace JellyfinMedic.Api;

[ApiController]
[Route("JellyfinMedic/Schedule")]
[Authorize(Policy = Policies.RequiresElevation)] // Admins only: these endpoints change server schedules.
public class ScheduleController : ControllerBase
{
    // The calendar can move forward up to 5 weeks, which always covers the next month.
    private const int MaxWeekOffset = 5;
    private const int MaxHistoryRows = 300;

    private readonly ITaskManager _taskManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ScheduleController> _logger;

    public ScheduleController(
        ITaskManager taskManager,
        IApplicationPaths appPaths,
        ILogger<ScheduleController> logger)
    {
        _taskManager = taskManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    // ---------- Calendar ----------

    [HttpGet("GetCalendar")]
    public ActionResult<object> GetCalendar([FromQuery] int weekOffset = 0)
    {
        weekOffset = Math.Clamp(weekOffset, 0, MaxWeekOffset);

        // Server local dates, the same clock Jellyfin's triggers run on.
        var now = DateTime.Now;
        var today = now.Date;
        var monday = today.AddDays(-ScheduleStorage.MondayFirst(today.DayOfWeek));
        var start = monday.AddDays(7 * weekOffset);

        var workers = VisibleTasks();
        var profile = ScheduleStorage.LoadProfile(_appPaths);
        var managed = ScheduleStorage.LoadManaged(_appPaths);
        var busy = Busy();

        var days = new List<object>();
        for (int i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var runs = new List<CalendarRun>();
            foreach (var worker in workers)
            {
                string id = worker.Id.ToString();
                profile.TryGetValue(id, out var rec);
                managed.TryGetValue(id, out var managedRun);
                runs.AddRange(RunsOn(worker, date, rec, managedRun));
            }

            days.Add(new
            {
                Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Name = date.ToString("ddd", CultureInfo.InvariantCulture),
                Label = date.ToString("d MMM", CultureInfo.InvariantCulture),
                IsToday = date == today,
                IsPast = date < today,
                Busy = busy.Shading(date.DayOfWeek),
                Runs = runs.OrderBy(r => r.SortMinutes).ThenBy(r => r.TaskName, StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        var end = start.AddDays(6);
        return Ok(new
        {
            WeekOffset = weekOffset,
            MaxWeekOffset,
            RangeLabel = $"{start.ToString("d MMM", CultureInfo.InvariantCulture)} – {end.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}",
            NowMinutes = (int)now.TimeOfDay.TotalMinutes,
            NowLabel = ScheduleStorage.Hhmm(now.TimeOfDay),
            TodayIndex = weekOffset == 0 ? ScheduleStorage.MondayFirst(today.DayOfWeek) : -1,
            BusySummary = busy.Summary,
            FromViewing = busy.FromViewing,
            SlotMinutes = BusyProfile.SlotMinutes,
            AvoidedSlots = busy.AvoidedSlots(),
            Days = days
        });
    }

    private static IEnumerable<CalendarRun> RunsOn(IScheduledTaskWorker worker, DateTime date, ReliabilityRecord? rec, ManagedRun? managed)
    {
        string schedule = ScheduleStorage.FormatSchedule(worker.Triggers, managed);
        var last = worker.LastExecutionResult;
        bool lastFailed = last?.Status == TaskCompletionStatus.Failed;
        bool backedOff = rec?.OriginalTriggers is not null;
        string clockKind = backedOff ? "backoff" : "normal";
        string category = ScheduleStorage.Category(worker, managed is not null);
        double duration = ScheduleStorage.TypicalMinutes(worker, rec);
        string avgText = rec is { TotalRuns: > 0 } ? ScheduleStorage.FormatDuration(rec.AverageDurationSeconds) : (last is not null ? ScheduleStorage.FormatDuration((last.EndTimeUtc - last.StartTimeUtc).TotalSeconds) : "Not known yet");
        string lastText = last is null
            ? "Hasn't run yet"
            : last.Status == TaskCompletionStatus.Failed
                ? "Failed" + (string.IsNullOrWhiteSpace(last.ErrorMessage) ? string.Empty : ": " + last.ErrorMessage)
                : last.Status.ToString();
        string? note = backedOff
            ? "Moved 3 hours later because it failed. Its usual time comes back after its next successful run."
            : managed is not null
                ? "Jellyfin has no monthly option, so Medic starts this one itself."
                : category == "plugin"
                    ? "Medic doesn't plan this task, so it's left where it is and other tasks are kept clear of it."
                    : null;

        CalendarRun Make(string time, int startMinutes, string kind) => new()
        {
            Time = time,
            SortMinutes = startMinutes,
            StartMinutes = startMinutes,
            DurationMinutes = Math.Round(duration, 1),
            TaskId = worker.Id.ToString(),
            TaskName = worker.Name,
            Schedule = schedule,
            Kind = kind,
            Category = kind == "managed" ? "monthly" : category,
            LastFailed = lastFailed,
            AverageText = avgText,
            LastText = lastText,
            Note = note
        };

        if (managed is not null)
        {
            if (date == ScheduleStorage.FirstWeekdayOfMonth(date.Year, date.Month, managed.Weekday))
            {
                var slot = TimeSpan.FromTicks(managed.TimeOfDayTicks);
                yield return Make(ScheduleStorage.Hhmm(slot), (int)slot.TotalMinutes, "managed");
            }

            yield break;
        }

        foreach (var t in worker.Triggers ?? Array.Empty<TaskTriggerInfo>())
        {
            if (t.Type == TaskTriggerInfoType.DailyTrigger && t.TimeOfDayTicks.HasValue)
            {
                var at = TimeSpan.FromTicks(t.TimeOfDayTicks.Value);
                yield return Make(ScheduleStorage.Hhmm(at), (int)at.TotalMinutes, clockKind);
            }
            else if (t.Type == TaskTriggerInfoType.WeeklyTrigger && t.TimeOfDayTicks.HasValue && t.DayOfWeek == date.DayOfWeek)
            {
                var at = TimeSpan.FromTicks(t.TimeOfDayTicks.Value);
                yield return Make(ScheduleStorage.Hhmm(at), (int)at.TotalMinutes, clockKind);
            }
            else if (t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks.HasValue && t.IntervalTicks.Value > 0)
            {
                var interval = TimeSpan.FromTicks(t.IntervalTicks.Value);
                if (interval < TimeSpan.FromHours(4))
                {
                    // Runs many times a day: shown as one note for the day rather than dozens of blocks.
                    yield return Make($"Every {ScheduleStorage.FormatInterval(interval)}", -1, "interval");
                }
                else
                {
                    // Interval triggers count from the last run, so the times are approximate.
                    var anchor = last is not null ? ScheduleStorage.AsUtc(last.EndTimeUtc).ToLocalTime() : DateTime.Now;
                    var next = anchor + interval;
                    int guard = 0;
                    while (next.Date < date && guard++ < 2000)
                    {
                        next += interval;
                    }

                    while (next.Date == date && guard++ < 2000)
                    {
                        yield return Make($"≈ {ScheduleStorage.Hhmm(next.TimeOfDay)}", (int)next.TimeOfDay.TotalMinutes, "interval");
                        next += interval;
                    }
                }
            }
        }
    }

    // ---------- Run history ----------

    [HttpGet("GetRunHistory")]
    public ActionResult<object> GetRunHistory([FromQuery] bool errorsOnly = false)
    {
        List<RunRecord> recorded;
        lock (ScheduleStorage.SyncRoot)
        {
            recorded = ScheduleStorage.ReadList<RunRecord>(ScheduleStorage.RunHistoryPath(_appPaths));
        }

        var workers = VisibleTasks();
        var profile = ScheduleStorage.LoadProfile(_appPaths);
        var managed = ScheduleStorage.LoadManaged(_appPaths);

        // Jellyfin keeps each task's last result. Add any we haven't recorded ourselves,
        // so the history isn't empty for runs from before Medic was listening.
        foreach (var worker in workers)
        {
            var last = worker.LastExecutionResult;
            if (last is null)
            {
                continue;
            }

            var lastRun = ScheduleStorage.ToRunRecord(worker, last);
            bool known = recorded.Any(r =>
                ScheduleStorage.SameId(r.TaskId, lastRun.TaskId) &&
                Math.Abs((r.EndUtc - lastRun.EndUtc).TotalSeconds) < 2);
            if (!known)
            {
                recorded.Add(lastRun);
            }
        }

        var runs = recorded
            .Where(r => !errorsOnly || IsError(r.Status))
            .OrderByDescending(r => r.EndUtc)
            .Take(MaxHistoryRows)
            .ToList();

        var summary = workers
            .Select(worker =>
            {
                string id = worker.Id.ToString();
                profile.TryGetValue(id, out var rec);
                managed.TryGetValue(id, out var managedRun);
                var last = worker.LastExecutionResult;

                return new
                {
                    TaskName = worker.Name,
                    Schedule = ScheduleStorage.FormatSchedule(worker.Triggers, managedRun),
                    LastEndUtc = last is null ? (DateTime?)null : ScheduleStorage.AsUtc(last.EndTimeUtc),
                    LastStatus = last?.Status.ToString(),
                    LastDurationSeconds = last is null ? (double?)null : Math.Max(0, (last.EndTimeUtc - last.StartTimeUtc).TotalSeconds),
                    AverageDurationSeconds = rec is { TotalRuns: > 0 } ? rec.AverageDurationSeconds : (double?)null,
                    RecordedRuns = rec?.TotalRuns ?? 0,
                    FailureRatePercent = rec is { TotalRuns: > 0 } ? rec.FailureRatePercent : (double?)null,
                    BackedOff = rec?.OriginalTriggers is not null
                };
            })
            .OrderBy(s => s.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { Summary = summary, Runs = runs });
    }

    private static bool IsError(string status) =>
        status.Equals(nameof(TaskCompletionStatus.Failed), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(nameof(TaskCompletionStatus.Aborted), StringComparison.OrdinalIgnoreCase);

    // ---------- Schedule changes log ----------

    [HttpGet("GetMovementAuditLog")]
    public ActionResult<IEnumerable<MovementAudit>> GetMovementAuditLog()
    {
        List<MovementAudit> logs;
        lock (ScheduleStorage.SyncRoot)
        {
            logs = ScheduleStorage.ReadList<MovementAudit>(ScheduleStorage.AuditLogPath(_appPaths));
        }

        return Ok(logs.OrderByDescending(l => l.Timestamp).Take(200));
    }

    // ---------- Preview and apply ----------

    [HttpGet("PreviewScheduleDiff")]
    public ActionResult<object> PreviewScheduleDiff()
    {
        var plan = BuildPlan();
        var rows = plan.Select(p => new
        {
            TaskName = p.Worker.Name,
            CurrentSchedule = p.CurrentSchedule,
            ProposedSchedule = p.ProposedSchedule,
            Cadence = p.CadenceLabel,
            Reason = p.Reason,
            Warning = p.Warning,
            Changes = p.Changes
        }).ToList();

        return Ok(new { TotalAuditedTasks = rows.Count, Changing = rows.Count(r => r.Changes), BusySummary = Busy().Summary, SchedulePlan = rows });
    }

    [HttpPost("ApplyRecommendedSchedule")]
    public ActionResult<object> ApplyRecommendedSchedule()
    {
        var changing = BuildPlan().Where(p => p.Changes).ToList();
        if (changing.Count == 0)
        {
            return Ok(new { Success = true, AppliedTasks = 0, Message = "Your tasks already match the recommended schedule, so nothing was changed." });
        }

        var (backupFile, _) = WriteBackup();
        var appliedIds = new List<string>();
        var failedNames = new List<string>();

        lock (ScheduleStorage.SyncRoot)
        {
            string managedPath = ScheduleStorage.ManagedPath(_appPaths);
            var managedRuns = ScheduleStorage.ReadList<ManagedRun>(managedPath);

            foreach (var item in changing)
            {
                string id = item.Worker.Id.ToString();
                try
                {
                    if (item.Managed is not null)
                    {
                        // Monthly: Jellyfin has no monthly trigger, so its own triggers are cleared
                        // and Medic starts the task on the day.
                        item.Worker.Triggers = Array.Empty<TaskTriggerInfo>();
                        item.Managed.LastStartedLocal ??= DateTime.Now; // First run is next month, not tonight.
                        managedRuns.RemoveAll(m => ScheduleStorage.SameId(m.TaskId, id));
                        managedRuns.Add(item.Managed);
                    }
                    else
                    {
                        // The same assignment Jellyfin's own dashboard uses: saves to disk and re-arms the timers.
                        item.Worker.Triggers = item.Triggers ?? Array.Empty<TaskTriggerInfo>();
                        managedRuns.RemoveAll(m => ScheduleStorage.SameId(m.TaskId, id));
                    }

                    appliedIds.Add(id);
                    ScheduleStorage.AppendAudit(_appPaths, item.Worker.Name, item.CurrentSchedule, item.ProposedSchedule, $"{item.CadenceLabel}. {item.Reason}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Jellyfin Medic: could not apply the new schedule to {TaskName}", item.Worker.Name);
                    failedNames.Add(item.Worker.Name);
                }
            }

            ScheduleStorage.WriteJson(managedPath, managedRuns);
        }

        ScheduleStorage.ClearBackoffs(_appPaths, appliedIds);
        _logger.LogInformation("Jellyfin Medic: applied recommended schedule to {Count} tasks. Backup: {BackupFile}", appliedIds.Count, backupFile);

        string message = $"Changed the schedule of {appliedIds.Count} tasks. Your previous schedule is saved as {backupFile}.";
        if (failedNames.Count > 0)
        {
            message += $" Could not update: {string.Join(", ", failedNames)}. See the Jellyfin log for details.";
        }

        return Ok(new { Success = failedNames.Count == 0, AppliedTasks = appliedIds.Count, BackupFile = backupFile, Message = message });
    }

    // ---------- Backups ----------

    [HttpGet("ListBackups")]
    public ActionResult<IEnumerable<BackupFileInfo>> ListBackups()
    {
        string dir = ScheduleStorage.BackupDir(_appPaths);
        if (!Directory.Exists(dir))
        {
            return Ok(Array.Empty<BackupFileInfo>());
        }

        var backups = new DirectoryInfo(dir)
            .GetFiles("schedule_backup_*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupFileInfo { FileName = f.Name, CreatedUtc = f.LastWriteTimeUtc })
            .ToList();

        return Ok(backups);
    }

    [HttpPost("BackupCurrentSchedule")]
    public ActionResult<object> BackupCurrentSchedule()
    {
        var (fileName, count) = WriteBackup();
        return Ok(new { Success = true, BackupFile = fileName, TotalBackedUp = count });
    }

    [HttpPost("RestorePreviousSchedule")]
    public ActionResult<object> RestorePreviousSchedule([FromQuery] string backupFileName)
    {
        // Only accept a bare file name, so the request can't reach outside the Backups folder.
        string safeName = Path.GetFileName(backupFileName ?? string.Empty);
        if (string.IsNullOrEmpty(safeName) || !safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { Success = false, Message = "Choose a backup file to restore." });
        }

        string targetPath = Path.Combine(ScheduleStorage.BackupDir(_appPaths), safeName);
        if (!System.IO.File.Exists(targetPath))
        {
            return NotFound(new { Success = false, Message = $"Backup {safeName} was not found." });
        }

        TaskBackupContainer? backup;
        try
        {
            backup = JsonSerializer.Deserialize<TaskBackupContainer>(System.IO.File.ReadAllText(targetPath));
        }
        catch (JsonException)
        {
            backup = null;
        }

        if (backup?.TriggersByTaskId is null || backup.TriggersByTaskId.Count == 0)
        {
            return BadRequest(new { Success = false, Message = $"Backup {safeName} is empty or unreadable, so nothing was changed." });
        }

        // Snapshot the current schedule first, so a restore can itself be undone.
        var (safetyBackup, _) = WriteBackup();

        var currentManaged = ScheduleStorage.LoadManaged(_appPaths);
        var backupManaged = (backup.ManagedRuns ?? new List<ManagedRun>())
            .GroupBy(m => m.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var restoredIds = new List<string>();
        var newManaged = new List<ManagedRun>();
        int changed = 0;
        int missing = 0;

        foreach (var (taskId, triggers) in backup.TriggersByTaskId)
        {
            var worker = ScheduleStorage.FindWorker(_taskManager, taskId);
            if (worker is null)
            {
                missing++; // Task no longer exists, e.g. its plugin was removed.
                continue;
            }

            string id = worker.Id.ToString();
            currentManaged.TryGetValue(id, out var managedNow);
            backupManaged.TryGetValue(id, out var managedThen);

            var target = triggers ?? Array.Empty<TaskTriggerInfo>();
            string before = ScheduleStorage.FormatSchedule(worker.Triggers, managedNow);
            string after = ScheduleStorage.FormatSchedule(target, managedThen);

            try
            {
                worker.Triggers = target;
                restoredIds.Add(id);

                if (managedThen is not null)
                {
                    managedThen.LastStartedLocal = DateTime.Now; // Don't fire straight away after a restore.
                    newManaged.Add(managedThen);
                }

                if (before != after)
                {
                    changed++;
                    ScheduleStorage.AppendAudit(_appPaths, worker.Name, before, after, $"Restored from backup {safeName}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellyfin Medic: could not restore the schedule for {TaskName}", worker.Name);
                if (managedNow is not null)
                {
                    newManaged.Add(managedNow); // Leave this task as it was.
                }
            }
        }

        // Tasks that weren't in the backup keep their current monthly schedule.
        newManaged.AddRange(currentManaged.Values.Where(m =>
            !backup.TriggersByTaskId.Keys.Any(k => ScheduleStorage.SameId(k, m.TaskId))));

        lock (ScheduleStorage.SyncRoot)
        {
            ScheduleStorage.WriteJson(ScheduleStorage.ManagedPath(_appPaths), newManaged);
        }

        ScheduleStorage.ClearBackoffs(_appPaths, restoredIds);
        _logger.LogInformation("Jellyfin Medic: restored {Count} tasks from {Backup} ({Changed} changed).", restoredIds.Count, safeName, changed);

        string message = $"Restored {safeName}: {changed} tasks changed back. Your schedule from just before this restore is saved as {safetyBackup}.";
        if (missing > 0)
        {
            message += $" {missing} tasks in the backup no longer exist and were skipped.";
        }

        return Ok(new { Success = true, RestoredTasks = restoredIds.Count, ChangedTasks = changed, BackupFile = safetyBackup, Message = message });
    }

    // ---------- Helpers ----------

    private IReadOnlyList<IScheduledTaskWorker> VisibleTasks() =>
        _taskManager.ScheduledTasks.Where(ScheduleStorage.IsVisible).ToList();

    private List<PlannedTask> BuildPlan() =>
        SchedulePlanner.Build(VisibleTasks(), ScheduleStorage.LoadProfile(_appPaths), ScheduleStorage.LoadManaged(_appPaths), Busy());

    private BusyProfile Busy() =>
        BusyProfile.Create(UsageAnalyzer.Summarise(UsageStore.Load(_appPaths)), Plugin.Instance?.Configuration);

    private (string FileName, int Count) WriteBackup()
    {
        var backup = new TaskBackupContainer
        {
            ManagedRuns = ScheduleStorage.LoadManaged(_appPaths).Values.ToList()
        };

        foreach (var task in _taskManager.ScheduledTasks)
        {
            backup.TriggersByTaskId[task.Id.ToString()] = task.Triggers?.ToArray() ?? Array.Empty<TaskTriggerInfo>();
        }

        string fileName = $"schedule_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        ScheduleStorage.WriteJson(Path.Combine(ScheduleStorage.BackupDir(_appPaths), fileName), backup);

        return (fileName, backup.TriggersByTaskId.Count);
    }
}

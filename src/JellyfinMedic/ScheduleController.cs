using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
            days.Add(new
            {
                Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Name = date.ToString("ddd", CultureInfo.InvariantCulture),
                Label = date.ToString("d MMM", CultureInfo.InvariantCulture),
                IsToday = date == today,
                IsPast = date < today,
                Busy = busy.Shading(date.DayOfWeek),
                Runs = ScheduleCalendar.PlannedRuns(workers, profile, managed, date, now)
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

    // ---------- Run history ----------

    [HttpGet("GetRunHistory")]
    public ActionResult<object> GetRunHistory([FromQuery] bool errorsOnly = false)
    {
        List<RunRecord> recorded;
        lock (ScheduleStorage.SyncRoot)
        {
            recorded = ScheduleStorage.ReadRuns(_appPaths);
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
            TaskId = p.Worker.Id.ToString(),
            Choice = p.Choice,
            CanPlan = p.CanPlan,
            CurrentSchedule = p.CurrentSchedule,
            ProposedSchedule = p.ProposedSchedule,
            Cadence = p.CadenceLabel,
            Reason = p.Reason,
            Warning = p.Warning,
            Changes = p.Changes
        }).ToList();

        return Ok(new
        {
            TotalAuditedTasks = rows.Count,
            Changing = rows.Count(r => r.Changes),
            BusySummary = Busy().Summary,
            PlanHash = PlanHash(plan),
            SchedulePlan = rows
        });
    }

    /// <summary>Tasks with no time to run at, what each does, and whether Medic recommends turning it on.</summary>
    [HttpGet("GetUntimedTasks")]
    public ActionResult<List<UntimedTaskAdvice>> GetUntimedTasks() =>
        Ok(SchedulePlanner.Untimed(VisibleTasks(), ScheduleStorage.LoadProfile(_appPaths), ScheduleStorage.LoadManaged(_appPaths)));

    /// <summary>Saves the owner's choice for one task: medic (let Medic schedule it), keep, or off.</summary>
    [HttpPost("SetChoice")]
    public ActionResult<object> SetChoice([FromQuery] string taskId, [FromQuery] string choice)
    {
        if (choice is not (ScheduleStorage.ChoiceMedic or ScheduleStorage.ChoiceKeep or ScheduleStorage.ChoiceOff) ||
            ScheduleStorage.FindWorker(_taskManager, taskId) is null)
        {
            return BadRequest();
        }

        ScheduleStorage.SaveChoice(_appPaths, taskId, choice);
        return Ok(new { Success = true });
    }

    /// <param name="planHash">The PlanHash from the preview the owner saw. Without it (an older page), the plan as it is now is applied.</param>
    [HttpPost("ApplyRecommendedSchedule")]
    public ActionResult<object> ApplyRecommendedSchedule([FromQuery] string? planHash = null)
    {
        var plan = BuildPlan();

        // Usage, settings, choices or tasks may have changed since the preview: never apply a plan the owner didn't see.
        if (!string.IsNullOrEmpty(planHash) && !string.Equals(planHash, PlanHash(plan), StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status409Conflict, "The plan changed since you previewed it. Preview again.");
        }

        var changing = plan.Where(p => p.Changes).ToList();
        if (changing.Count == 0)
        {
            return Ok(new { Success = true, AppliedTasks = 0, Message = "Your tasks already match the recommended schedule, so nothing was changed." });
        }

        var (backupFile, _) = WriteBackup();
        var appliedIds = new List<string>();
        var failedNames = new List<string>();
        var audits = new List<MovementAudit>();

        lock (ScheduleStorage.SyncRoot)
        {
            // Read, change and write the monthly runs under one lock, so a run the background service
            // records meanwhile isn't lost. They're saved before any triggers change: if saving fails,
            // nothing has changed yet, and a monthly task is never left with no triggers and no run.
            string managedPath = ScheduleStorage.ManagedPath(_appPaths);
            var current = ScheduleStorage.ReadList<ManagedRun>(managedPath);
            var managedRuns = current.Where(m => !changing.Any(c => ScheduleStorage.SameId(m.TaskId, c.Worker.Id.ToString()))).ToList();
            foreach (var item in changing.Where(c => c.Managed is not null))
            {
                // The plan was made from an earlier read: a run started since then still counts this month.
                var now = current.LastOrDefault(m => ScheduleStorage.SameId(m.TaskId, item.Worker.Id.ToString()));
                item.Managed!.LastStartedLocal = Later(now?.LastStartedLocal, item.Managed.LastStartedLocal)
                    ?? DateTime.Now; // First run is next month, not tonight.
                managedRuns.Add(item.Managed);
            }

            if (!TryWriteManaged(managedPath, managedRuns))
            {
                return Ok(new
                {
                    Success = false,
                    AppliedTasks = 0,
                    BackupFile = backupFile,
                    Message = "Couldn't save Medic's schedule file, so nothing was changed. See the Jellyfin log for details."
                });
            }

            bool putBack = false;
            foreach (var item in changing)
            {
                string id = item.Worker.Id.ToString();
                try
                {
                    // Monthly: Jellyfin has no monthly trigger, so its own triggers are cleared and Medic
                    // starts the task on the day. Otherwise the same assignment Jellyfin's own dashboard
                    // uses: saves to disk and re-arms the timers.
                    item.Worker.Triggers = item.Managed is not null
                        ? Array.Empty<TaskTriggerInfo>()
                        : item.Triggers ?? Array.Empty<TaskTriggerInfo>();
                    appliedIds.Add(id);
                    audits.Add(ScheduleStorage.Audit(item.Worker.Name, item.CurrentSchedule, item.ProposedSchedule, $"{item.CadenceLabel}. {item.Reason}"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Jellyfin Medic: could not apply the new schedule to {TaskName}", item.Worker.Name);
                    failedNames.Add(item.Worker.Name);

                    // Its triggers didn't change, so its monthly run goes back to what it was too.
                    managedRuns.RemoveAll(m => ScheduleStorage.SameId(m.TaskId, id));
                    managedRuns.AddRange(current.Where(m => ScheduleStorage.SameId(m.TaskId, id)));
                    putBack = true;
                }
            }

            if (putBack)
            {
                TryWriteManaged(managedPath, managedRuns);
            }
        }

        WriteAudits(audits);
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
        var (safetyBackup, _) = WriteBackup(keep: safeName);

        var backupManaged = (backup.ManagedRuns ?? new List<ManagedRun>())
            .GroupBy(m => m.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var restoredIds = new List<string>();
        var audits = new List<MovementAudit>();
        int changed = 0;
        int missing = 0;

        lock (ScheduleStorage.SyncRoot)
        {
            // As in Apply: read, change and write the monthly runs under one lock, and save them before
            // any triggers change.
            string managedPath = ScheduleStorage.ManagedPath(_appPaths);
            var current = ScheduleStorage.ReadList<ManagedRun>(managedPath);
            var currentManaged = current
                .GroupBy(m => m.TaskId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            var targets = new List<(IScheduledTaskWorker Worker, TaskTriggerInfo[] Triggers, ManagedRun? Now, ManagedRun? Then)>();
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
                targets.Add((worker, triggers ?? Array.Empty<TaskTriggerInfo>(), managedNow, managedThen));
            }

            // Tasks that weren't in the backup keep their current monthly schedule.
            var managedRuns = current.Where(m => !backup.TriggersByTaskId.Keys.Any(k => ScheduleStorage.SameId(k, m.TaskId))).ToList();
            foreach (var target in targets.Where(t => t.Then is not null))
            {
                target.Then!.LastStartedLocal = RestoredLastStart(target.Then, target.Now, DateTime.Now);
                managedRuns.Add(target.Then);
            }

            if (!TryWriteManaged(managedPath, managedRuns))
            {
                return Ok(new
                {
                    Success = false,
                    RestoredTasks = 0,
                    ChangedTasks = 0,
                    BackupFile = safetyBackup,
                    Message = "Couldn't save Medic's schedule file, so nothing was restored. See the Jellyfin log for details."
                });
            }

            bool putBack = false;
            foreach (var (worker, triggers, managedNow, managedThen) in targets)
            {
                string id = worker.Id.ToString();
                string before = ScheduleStorage.FormatSchedule(worker.Triggers, managedNow);
                string after = ScheduleStorage.FormatSchedule(triggers, managedThen);

                try
                {
                    worker.Triggers = triggers;
                    restoredIds.Add(id);

                    if (before != after)
                    {
                        changed++;
                        audits.Add(ScheduleStorage.Audit(worker.Name, before, after, $"Restored from backup {safeName}."));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Jellyfin Medic: could not restore the schedule for {TaskName}", worker.Name);

                    // Leave this task as it was, monthly run included.
                    managedRuns.RemoveAll(m => ScheduleStorage.SameId(m.TaskId, id));
                    if (managedNow is not null)
                    {
                        managedRuns.Add(managedNow);
                    }

                    putBack = true;
                }
            }

            if (putBack)
            {
                TryWriteManaged(managedPath, managedRuns);
            }
        }

        WriteAudits(audits);
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
        SchedulePlanner.Build(VisibleTasks(), ScheduleStorage.LoadProfile(_appPaths), ScheduleStorage.LoadManaged(_appPaths), Busy(), ScheduleStorage.LoadChoices(_appPaths));

    // Covers what Apply would do: each task's choice and whether it changes, and for a task that changes, its cadence,
    // new triggers and monthly day and time. Wording, reasons and the current schedule are left out, so only a
    // different outcome changes the hash. Ordered by task id and written with the invariant culture, so the same plan
    // always gives the same hash.
    internal static string PlanHash(IEnumerable<PlannedTask> plan)
    {
        var text = new StringBuilder();
        foreach (var task in plan.OrderBy(p => p.Worker.Id.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            text.Append(CultureInfo.InvariantCulture, $"{task.Worker.Id.ToString().ToLowerInvariant()}|{task.Choice}|{task.Changes}");
            if (task.Changes)
            {
                text.Append(CultureInfo.InvariantCulture, $"|{task.Cadence}");
                foreach (var t in task.Triggers ?? Array.Empty<TaskTriggerInfo>())
                {
                    text.Append(CultureInfo.InvariantCulture, $"|{t.Type},{t.TimeOfDayTicks},{t.IntervalTicks},{t.DayOfWeek},{t.MaxRuntimeTicks}");
                }

                if (task.Managed is { } managed)
                {
                    text.Append(CultureInfo.InvariantCulture, $"|monthly,{managed.Weekday},{managed.TimeOfDayTicks}");
                }
            }

            text.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private BusyProfile Busy() =>
        BusyProfile.Create(UsageAnalyzer.Summarise(UsageStore.Load(_appPaths)), Plugin.Instance?.Configuration,
            HostLoadAnalyzer.Summarise(HostLoadStore.Load(_appPaths)));

    private (string FileName, int Count) WriteBackup(string? keep = null)
    {
        var backup = new TaskBackupContainer
        {
            ManagedRuns = ScheduleStorage.LoadManaged(_appPaths).Values.ToList()
        };

        foreach (var task in _taskManager.ScheduledTasks)
        {
            backup.TriggersByTaskId[task.Id.ToString()] = task.Triggers?.ToArray() ?? Array.Empty<TaskTriggerInfo>();
        }

        // Down to the millisecond: two backups in the same second used to share a name, so the second
        // replaced the first (a restore straight after an apply lost the schedule from before the apply).
        string fileName = $"schedule_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json";
        ScheduleStorage.WriteJson(Path.Combine(ScheduleStorage.BackupDir(_appPaths), fileName), backup);
        ScheduleStorage.PruneBackups(_appPaths, keep);

        return (fileName, backup.TriggersByTaskId.Count);
    }

     // False when the file couldn't be written; the error is logged.
    private bool TryWriteManaged(string path, List<ManagedRun> runs)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ScheduleStorage.WriteJson(path, runs);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Jellyfin Medic: could not save the monthly task schedule to {Path}", path);
            return false;
        }
    }

    // The schedule has already changed by now, so a log that can't be written is reported, not fatal.
    private void WriteAudits(List<MovementAudit> audits)
    {
        try
        {
            ScheduleStorage.AppendAudits(_appPaths, audits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Jellyfin Medic: could not add {Count} entries to the schedule changes log", audits.Count);
        }
    }

    // While this month's run of a restored monthly task is still ahead, its real last start (the later of the
    // current record's and the backup's) decides, so it runs then unless it already ran this month. Once that
    // run has passed, it counts as started now, so it doesn't fire straight away and first runs next month.
    internal static DateTime? RestoredLastStart(ManagedRun restored, ManagedRun? current, DateTime now) =>
        now < ManagedScheduleRunner.DueThisMonth(restored, now)
            ? Later(current?.LastStartedLocal, restored.LastStartedLocal)
            : now;

    private static DateTime? Later(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);
}

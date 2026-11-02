using MediaBrowser.Common.Configuration;
using Jellyfin.Data.Events;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Watches every scheduled task run. It records each run (with any error) for the
/// Run history tab, keeps average run times for the planner, and moves a failing
/// task 3 hours later until it next succeeds.
/// Started by PluginServiceRegistrator in Plugin.cs.
/// </summary>
public sealed class TaskLifecycleListener : IHostedService
{
    private static readonly TimeSpan BackoffShift = TimeSpan.FromHours(3);

    private readonly ITaskManager _taskManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<TaskLifecycleListener> _logger;

    public TaskLifecycleListener(
        ITaskManager taskManager,
        IApplicationPaths appPaths,
        ILogger<TaskLifecycleListener> logger)
    {
        _taskManager = taskManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;
        _taskManager.TaskExecuting += OnTaskExecuting;
        _logger.LogInformation("Jellyfin Medic: listening for scheduled task results.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        _taskManager.TaskExecuting -= OnTaskExecuting;
        ScheduleStorage.FlushRuns(_appPaths);
        return Task.CompletedTask;
    }

    private void OnTaskExecuting(object? sender, GenericEventArgs<IScheduledTaskWorker> e) =>
        ServerNow.TaskStarted(e.Argument.Id.ToString());

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        // Never let an error in here bubble back into Jellyfin's task engine.
        ServerNow.TaskFinished(e.Task.Id.ToString());
        try
        {
            HandleCompletion(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellyfin Medic: error while processing a completed task.");
        }
    }

    private void HandleCompletion(TaskCompletionEventArgs e)
    {
        var worker = e.Task;
        var result = e.Result;
        if (worker is null || result is null)
        {
            return;
        }

        // Every run goes into the history, whatever its result.
        ScheduleStorage.AppendRun(_appPaths, ScheduleStorage.ToRunRecord(worker, result));

        // Cancelled and aborted runs are usually you pressing stop or the server restarting,
        // not a problem with the task, so they don't count towards the stats or a back-off.
        if (result.Status is not (TaskCompletionStatus.Completed or TaskCompletionStatus.Failed))
        {
            return;
        }

        bool failed = result.Status == TaskCompletionStatus.Failed;
        double durationSeconds = Math.Max(0, (result.EndTimeUtc - result.StartTimeUtc).TotalSeconds);
        string taskId = worker.Id.ToString();

        lock (ScheduleStorage.SyncRoot)
        {
            string profilePath = ScheduleStorage.ProfilePath(_appPaths);
            var records = ScheduleStorage.ReadList<ReliabilityRecord>(profilePath);

            var entry = records.FirstOrDefault(r => ScheduleStorage.SameId(r.TaskKey, taskId));
            if (entry is null)
            {
                entry = new ReliabilityRecord { TaskKey = taskId };
                records.Add(entry);
            }

            entry.TaskName = worker.Name;
            entry.TotalRuns++;
            if (failed)
            {
                entry.Failures++;
            }

            // Only completed runs count towards the average: a run that fails early would make the
            // task look quick, and the planner would then give it too short a slot.
            if (!failed)
            {
                int completed = Math.Max(1, entry.TotalRuns - entry.Failures);
                entry.AverageDurationSeconds = Math.Round(((entry.AverageDurationSeconds * (completed - 1)) + durationSeconds) / completed, 1);
            }

            if (failed && entry.OriginalTriggers is null)
            {
                ApplyBackoff(worker, entry, () => ScheduleStorage.WriteJson(profilePath, records));
            }
            else if (!failed && entry.OriginalTriggers is not null)
            {
                RevertBackoff(worker, entry);
            }

            ScheduleStorage.WriteJson(profilePath, records);
        }
    }

    /// <summary>
    /// Moves the task's clock-time triggers 3 hours later and remembers the original
    /// schedule. Only happens once: a task that is already backed off isn't moved again,
    /// so repeated failures can't push it round the clock into daytime.
    /// A task its owner chose to keep in Preview stays where they put it, and so does a
    /// task that would then start in the hours Medic avoids.
    /// Monthly tasks run by Medic have no Jellyfin triggers, so they're never moved.
    /// </summary>
    private void ApplyBackoff(IScheduledTaskWorker worker, ReliabilityRecord entry, Action saveProfile)
    {
        if (ScheduleStorage.LoadChoices(_appPaths).GetValueOrDefault(worker.Id.ToString()) == ScheduleStorage.ChoiceKeep)
        {
            return;
        }

        var current = worker.Triggers?.ToArray() ?? Array.Empty<TaskTriggerInfo>();
        var shifted = Shifted(current, Plugin.Instance?.Configuration);
        if (shifted is null)
        {
            return;
        }

        string before = ScheduleStorage.FormatTriggers(current);
        string after = ScheduleStorage.FormatTriggers(shifted);

        // Both records are saved before the task moves: a move whose original time was lost would never
        // be undone. When the task stays after all, HandleCompletion's save clears the record again.
        entry.OriginalTriggers = current;
        saveProfile();
        if (!TryAudit(worker.Name, before, after, "Task failed. Moved 3 hours later; the original time comes back after its next successful run."))
        {
            entry.OriginalTriggers = null;
            _logger.LogWarning("Jellyfin Medic: '{TaskName}' failed, but stays at {Current} because the move couldn't be added to the schedule changes log.", worker.Name, before);
            return;
        }

        try
        {
            worker.Triggers = shifted;
        }
        catch (Exception ex)
        {
            entry.OriginalTriggers = null;
            _logger.LogError(ex, "Jellyfin Medic: could not move {TaskName} after it failed.", worker.Name);

            // The log already has the move, so it gets the task's own time back too.
            TryAudit(worker.Name, after, before, "Jellyfin couldn't save the move, so the task keeps its time.");
            return;
        }

        _logger.LogWarning(
            "Jellyfin Medic: '{TaskName}' failed. Schedule moved from {Before} to {After} until it next succeeds.",
            worker.Name,
            before,
            after);
    }

    /// <summary>
    /// Puts back the schedule saved by ApplyBackoff once the task succeeds again, but only while the
    /// task still has the schedule the back-off gave it. If someone changed it since (in Jellyfin's
    /// dashboard, say), that change stays and the back-off is simply over. Either way the back-off
    /// ends, even when the schedule changes log can't be written: one left in place would hold off the next.
    /// </summary>
    private void RevertBackoff(IScheduledTaskWorker worker, ReliabilityRecord entry)
    {
        var original = entry.OriginalTriggers!;
        string before = ScheduleStorage.FormatTriggers(worker.Triggers);

        if (!SameTriggers(worker.Triggers ?? Array.Empty<TaskTriggerInfo>(), original.Select(Shift)))
        {
            entry.OriginalTriggers = null;
            TryAudit(
                worker.Name,
                before,
                before,
                "Task succeeded after a back-off, but its schedule had been changed since, so the change was kept.");
            _logger.LogInformation("Jellyfin Medic: '{TaskName}' succeeded. Its schedule was changed during the back-off, so it stays at {Current}.", worker.Name, before);
            return;
        }

        try
        {
            worker.Triggers = original;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellyfin Medic: could not put back the original schedule for {TaskName}.", worker.Name);
            return;
        }

        entry.OriginalTriggers = null;

        string after = ScheduleStorage.FormatTriggers(original);
        TryAudit(
            worker.Name,
            before,
            after,
            "Task succeeded after a back-off. Original schedule put back.");

        _logger.LogInformation("Jellyfin Medic: '{TaskName}' succeeded. Schedule returned to {After}.", worker.Name, after);
    }

    // False when the schedule changes log couldn't be written; the error is logged.
    private bool TryAudit(string taskName, string before, string after, string rationale)
    {
        try
        {
            ScheduleStorage.AppendAudit(_appPaths, taskName, before, after, rationale);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Jellyfin Medic: could not add {TaskName} to the schedule changes log.", taskName);
            return false;
        }
    }

    /// <summary>
    /// The triggers 3 hours later, or null when none of them has a clock time to move or one of them
    /// would then start in the hours Medic avoids. Only the start time is checked, not the whole run.
    /// </summary>
    internal static TaskTriggerInfo[]? Shifted(IReadOnlyList<TaskTriggerInfo> triggers, PluginConfiguration? settings)
    {
        // Interval-only tasks have no clock time to move.
        if (!triggers.Any(t => t.TimeOfDayTicks.HasValue))
        {
            return null;
        }

        var shifted = triggers.Select(Shift).ToArray();
        return shifted.Any(t => t.TimeOfDayTicks is { } ticks && InAvoidedHours(ticks, settings)) ? null : shifted;
    }

    // The hours to avoid run past midnight when they end before they start, 22:00 to 02:00 say.
    private static bool InAvoidedHours(long timeOfDayTicks, PluginConfiguration? settings)
    {
        if (settings is not { AvoidEnabled: true })
        {
            return false;
        }

        int from = BusyProfile.AvoidStartMinute(settings);
        int to = BusyProfile.AvoidEndMinute(settings);
        int minute = (int)TimeSpan.FromTicks(timeOfDayTicks).TotalMinutes;
        return from <= to ? minute >= from && minute < to : minute >= from || minute < to;
    }

    // The same triggers, in any order.
    private static bool SameTriggers(IEnumerable<TaskTriggerInfo> a, IEnumerable<TaskTriggerInfo> b) =>
        a.Select(Describe).Order(StringComparer.Ordinal).SequenceEqual(b.Select(Describe).Order(StringComparer.Ordinal));

    private static string Describe(TaskTriggerInfo t) =>
        $"{t.Type}|{t.TimeOfDayTicks}|{t.IntervalTicks}|{t.DayOfWeek}|{t.MaxRuntimeTicks}";

    private static TaskTriggerInfo Shift(TaskTriggerInfo trigger)
    {
        if (!trigger.TimeOfDayTicks.HasValue)
        {
            return trigger;
        }

        var time = TimeSpan.FromTicks(trigger.TimeOfDayTicks.Value) + BackoffShift;
        var day = trigger.DayOfWeek;

        // Past midnight: wrap the time, and for weekly triggers move to the next day too.
        if (time >= TimeSpan.FromDays(1))
        {
            time -= TimeSpan.FromDays(1);
            if (day.HasValue)
            {
                day = (DayOfWeek)(((int)day.Value + 1) % 7);
            }
        }

        return new TaskTriggerInfo
        {
            Type = trigger.Type,
            TimeOfDayTicks = time.Ticks,
            IntervalTicks = trigger.IntervalTicks,
            MaxRuntimeTicks = trigger.MaxRuntimeTicks,
            DayOfWeek = day
        };
    }
}

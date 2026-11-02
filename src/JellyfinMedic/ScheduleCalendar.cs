using MediaBrowser.Model.Tasks;

namespace JellyfinMedic.Api;

/// <summary>
/// The task runs planned on a day, from each task's triggers. The Schedule tab's calendar and the
/// Dashboard's "Today" both use it, so they never disagree.
/// </summary>
internal static class ScheduleCalendar
{
    /// <summary>Every given task's planned runs on <paramref name="date"/> (server local), in time order.</summary>
    internal static List<CalendarRun> PlannedRuns(
        IEnumerable<IScheduledTaskWorker> workers,
        IReadOnlyDictionary<string, ReliabilityRecord> profile,
        IReadOnlyDictionary<string, ManagedRun> managed,
        DateTime date,
        DateTime now)
    {
        var runs = new List<CalendarRun>();
        foreach (var worker in workers)
        {
            string id = worker.Id.ToString();
            profile.TryGetValue(id, out var rec);
            managed.TryGetValue(id, out var managedRun);
            runs.AddRange(RunsOn(worker, date, rec, managedRun, now));
        }

        return runs.OrderBy(r => r.SortMinutes).ThenBy(r => r.TaskName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // `now` (server local) anchors an interval task that has never run.
    private static IEnumerable<CalendarRun> RunsOn(IScheduledTaskWorker worker, DateTime date, ReliabilityRecord? rec, ManagedRun? managed, DateTime now)
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

                // ManagedScheduleRunner skips the slot when the task already started earlier that month (on the
                // day it had before it was moved, or when it was handed to Medic). A start from the slot on is
                // this month's run itself, which stays on the calendar.
                bool startedEarlier = managed.LastStartedLocal is { } started && started >= new DateTime(date.Year, date.Month, 1) && started < date + slot;
                if (!startedEarlier)
                {
                    yield return Make(ScheduleStorage.Hhmm(slot), (int)slot.TotalMinutes, "managed");
                }
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
                    var anchor = last is not null ? ScheduleStorage.AsUtc(last.EndTimeUtc).ToLocalTime() : now;
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
}

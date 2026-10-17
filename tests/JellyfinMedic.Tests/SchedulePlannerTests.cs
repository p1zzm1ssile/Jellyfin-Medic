using JellyfinMedic.Api;
using JellyfinMedic.Services;
using MediaBrowser.Model.Tasks;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

public class SchedulePlannerTests
{
    private static readonly Dictionary<string, ReliabilityRecord> NoHistory = new();

    [Fact]
    public void A_kept_task_that_runs_past_midnight_is_not_planned_over()
    {
        // Free from 23:00 to 03:30. A kept task takes 23:45 to about 03:30 every night,
        // so 23:00 to 23:45 is the only room left.
        var kept = new FakeTaskWorker("Someone else's task", "SomeoneElsesTask", 180, Daily(23, 45));
        var planned = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { kept, planned }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(23, 0, 3, 30));

        Assert.InRange(plan.Single(p => p.Worker == planned).StartMinutes, (23 * 60) + 0, (23 * 60) + 30);
    }

    [Fact]
    public void A_long_task_can_start_before_midnight_and_run_on()
    {
        // Free from 22:00 to 02:00. Three hours plus the buffer needs 3 h 45 min, which only fits across midnight.
        var scan = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 180, Daily(2, 0));

        var task = SchedulePlanner.Build(new[] { scan }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(22, 0, 2, 0)).Single();

        Assert.Equal(Cadence.Daily, task.Cadence);
        Assert.InRange(task.StartMinutes, 22 * 60, (22 * 60) + 15);
    }

    [Fact]
    public void A_kept_task_between_quarter_hours_blocks_all_of_its_run()
    {
        // Free from 23:45 to 00:15. The kept task runs 23:50 to 00:05 (with its buffer), so there's no room.
        var kept = new FakeTaskWorker("Someone else's task", "SomeoneElsesTask", 10, Daily(23, 50));
        var planned = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { kept, planned }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(23, 45, 0, 15));

        Assert.Equal(Cadence.LeaveAlone, plan.Single(p => p.Worker == planned).Cadence);
    }

    [Fact]
    public void A_kept_task_that_Medic_runs_monthly_keeps_its_time()
    {
        // Free from 01:00 to 01:15. Another kept task takes that quarter hour on every day but Wednesday,
        // and on Wednesday it's taken by a task Medic runs once a month, so there's no room.
        var weekly = new FakeTaskWorker("Someone else's task", "SomeoneElsesTask", 1,
            Enum.GetValues<DayOfWeek>().Where(d => d != DayOfWeek.Wednesday).Select(d => Weekly(d, 1, 0)).ToArray());
        var monthly = new FakeTaskWorker("A monthly task", "AMonthlyTask", 1);
        var managed = new Dictionary<string, ManagedRun>
        {
            [monthly.Id] = new ManagedRun { TaskId = monthly.Id, TaskName = monthly.Name, Weekday = DayOfWeek.Wednesday, TimeOfDayTicks = TimeSpan.FromHours(1).Ticks }
        };
        var planned = new FakeTaskWorker("Optimize database", "OptimizeDatabaseTask", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { weekly, monthly, planned }, NoHistory, managed, FreeBetween(1, 0, 1, 15));

        Assert.Equal(Cadence.LeaveAlone, plan.Single(p => p.Worker == planned).Cadence);
    }

    [Fact]
    public void A_task_with_no_free_slot_is_listed_after_the_planned_ones()
    {
        // Free from 23:45 to 00:15: room for the transcode clean-up, but not for a three-hour scan.
        var scan = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 180, Daily(2, 0));
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { scan, clean }, NoHistory, new Dictionary<string, ManagedRun>(), FreeBetween(23, 45, 0, 15));

        Assert.Equal(Cadence.LeaveAlone, plan.Single(p => p.Worker == scan).Cadence);
        Assert.Equal(new[] { clean.Name, scan.Name }, plan.Select(p => p.Worker.Name));
    }

    [Fact]
    public void Hours_Medic_has_never_seen_are_not_planned_into()
    {
        // The server is switched off from 00:00 to 08:00, so Medic has no readings then. Those hours
        // must not count as the quietest of the day.
        var scan = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 180, Daily(2, 0));
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var plan = SchedulePlanner.Build(new[] { scan, clean }, NoHistory, new Dictionary<string, ManagedRun>(), OffAtNight(null));

        foreach (var (task, minutes) in new[] { (scan, 180), (clean, 1) })
        {
            var planned = plan.Single(p => p.Worker == task);
            Assert.Equal(Cadence.Daily, planned.Cadence);
            Assert.All(planned.Triggers!, t => Assert.InRange(TimeSpan.FromTicks(t.TimeOfDayTicks!.Value).TotalMinutes, 8 * 60, (24 * 60) - minutes));
        }
    }

    [Fact]
    public void A_task_that_only_fits_in_hours_Medic_has_never_seen_keeps_its_time()
    {
        // Off from 00:00 to 08:00, and the rest of the day is avoided, so only unseen hours are free.
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));
        var avoidDaytime = new PluginConfiguration { AvoidEnabled = true, AvoidStartMinute = 8 * 60, AvoidEndMinute = 0 };

        var task = SchedulePlanner.Build(new[] { clean }, NoHistory, new Dictionary<string, ManagedRun>(), OffAtNight(avoidDaytime)).Single();

        Assert.Equal(Cadence.LeaveAlone, task.Cadence);
        Assert.False(task.Changes);
        Assert.Equal(task.CurrentSchedule, task.ProposedSchedule);
        Assert.Contains("never seen the server on", task.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_stays_put_when_the_new_time_is_only_slightly_quieter()
    {
        // 13:30 to 15:15 is quieter by 0.1 person every day: 7 runs of 15 minutes save 0.175
        // person-hours a week, under the 0.25 a move has to save.
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var task = SchedulePlanner.Build(new[] { clean }, NoHistory, new Dictionary<string, ManagedRun>(), QuieterAfternoon(0.1)).Single();

        Assert.Equal(3 * 60, task.StartMinutes);
        Assert.False(task.Changes);
    }

    [Fact]
    public void A_task_moves_when_the_new_time_is_clearly_quieter()
    {
        // Quieter by 0.2 person: 0.35 person-hours a week, over the 0.25 a move has to save.
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var task = SchedulePlanner.Build(new[] { clean }, NoHistory, new Dictionary<string, ManagedRun>(), QuieterAfternoon(0.2)).Single();

        Assert.InRange(task.StartMinutes, (13 * 60) + 30, 15 * 60);
        Assert.True(task.Changes);
    }

    [Fact]
    public void A_task_kept_at_a_busy_time_says_a_slightly_quieter_time_was_not_worth_the_move()
    {
        // 0.6 people watch at 03:00, and 13:30 to 15:15 is only 0.1 quieter.
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var task = SchedulePlanner.Build(new[] { clean }, NoHistory, new Dictionary<string, ManagedRun>(), QuieterAfternoon(0.1, watching: 0.6)).Single();

        Assert.Equal(3 * 60, task.StartMinutes);
        Assert.DoesNotContain("no quieter free time", task.Warning, StringComparison.Ordinal);
        Assert.Contains("slightly quieter time", task.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_at_a_busy_time_with_nothing_quieter_still_says_so()
    {
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));

        var task = SchedulePlanner.Build(new[] { clean }, NoHistory, new Dictionary<string, ManagedRun>(), QuieterAfternoon(0, watching: 0.6)).Single();

        Assert.Contains("There was no quieter free time for it.", task.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("slightly quieter time", task.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_that_keeps_its_time_keeps_its_triggers()
    {
        // The same time on every day, written as seven weekly triggers rather than one daily one.
        var weekly = Enum.GetValues<DayOfWeek>().Select(d => Weekly(d, 3, 0)).ToArray();
        var clean = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, weekly);

        var task = SchedulePlanner.Build(new[] { clean }, NoHistory, new Dictionary<string, ManagedRun>(), QuieterAfternoon(0.1)).Single();

        Assert.False(task.Changes);
        Assert.Equal(task.CurrentSchedule, task.ProposedSchedule);
        Assert.Equal(7, task.Triggers!.Length);
        Assert.All(task.Triggers, t => Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, t.Type));
    }

    // Three weeks of readings, none between 00:00 and 08:00; quiet in the day, busy in the evening.
    private static BusyProfile OffAtNight(PluginConfiguration? settings) =>
        Learned(hour => hour < 8 ? null : hour < 18 ? 0.3 : 1.0, settings);

    // Three weeks of readings with the same number of people watching all day, fewer from 13:00 to 16:00.
    private static BusyProfile QuieterAfternoon(double quieterBy, double watching = 0.2) =>
        Learned(hour => hour is >= 13 and < 16 ? watching - quieterBy : watching, null);

    // Usage learned by hour of day, the same every day; null for an hour with no readings.
    private static BusyProfile Learned(Func<int, double?> watching, PluginConfiguration? settings)
    {
        var week = Enumerable.Range(0, 168).Select(i => watching(i % 24)).ToList();
        var usage = new UsageSummary
        {
            HoursCollected = 21 * 24,
            Ready = true,
            AverageStreams = week.Select(w => w ?? 0).ToList(),
            SampleCounts = week.Select(w => w is null ? 0 : 36).ToList()
        };

        return BusyProfile.Create(usage, settings);
    }

    // Everything outside the given window is set as "avoid", so the planner can only use the window.
    private static BusyProfile FreeBetween(int fromHour, int fromMinute, int toHour, int toMinute) =>
        BusyProfile.Create(new UsageSummary(), new PluginConfiguration
        {
            AvoidEnabled = true,
            AvoidStartMinute = (toHour * 60) + toMinute,
            AvoidEndMinute = (fromHour * 60) + fromMinute
        });
}

public sealed class MonthlyTaskTests
{
    // October 2026 starts on a Thursday, so its first Saturday is the 3rd and its first Wednesday the 7th.
    // September's first Wednesday is the 2nd, and November's the 4th.
    private static readonly DateTime FirstSaturdayOfOctober = new(2026, 10, 3, 3, 0, 0);
    private static readonly DateTime FirstWednesdayOfSeptember = new(2026, 9, 2, 3, 0, 0);
    private static readonly DateTime FirstWednesdayOfOctober = new(2026, 10, 7, 3, 0, 0);
    private static readonly DateTime FirstWednesdayOfNovember = new(2026, 11, 4, 3, 0, 0);

    [Fact]
    public void A_monthly_task_moved_to_a_later_day_does_not_run_again_that_month()
    {
        // It ran on the first Saturday, then was moved to the first Wednesday.
        var run = OnFirstWednesday(lastStarted: FirstSaturdayOfOctober);

        Assert.False(ManagedScheduleRunner.IsDue(run, FirstWednesdayOfOctober));
        Assert.True(ManagedScheduleRunner.IsDue(run, FirstWednesdayOfNovember));
    }

    [Fact]
    public void The_calendar_leaves_a_moved_monthly_task_out_of_a_month_it_already_ran_in()
    {
        var task = new FakeTaskWorker("A monthly task", "AMonthlyTask", 1);
        var managed = new Dictionary<string, ManagedRun> { [task.Id] = OnFirstWednesday(lastStarted: FirstSaturdayOfOctober) };

        Assert.Empty(PlannedRuns(task, managed, FirstWednesdayOfOctober.Date));
        Assert.Equal(3 * 60, Assert.Single(PlannedRuns(task, managed, FirstWednesdayOfNovember.Date)).StartMinutes);

        // Once it has run on its day, that run stays on the calendar.
        managed[task.Id].LastStartedLocal = FirstWednesdayOfNovember;
        Assert.Single(PlannedRuns(task, managed, FirstWednesdayOfNovember.Date));
    }

    [Fact]
    public void A_task_restored_before_its_day_runs_that_month()
    {
        var restored = Restore(OnFirstWednesday(lastStarted: FirstWednesdayOfSeptember), current: null, new DateTime(2026, 10, 5, 12, 0, 0));
        Assert.True(ManagedScheduleRunner.IsDue(restored, FirstWednesdayOfOctober));

        // Restored on its day an hour before its time, it still runs then.
        var onItsDay = Restore(OnFirstWednesday(lastStarted: FirstWednesdayOfSeptember), current: null, new DateTime(2026, 10, 7, 2, 0, 0));
        Assert.True(ManagedScheduleRunner.IsDue(onItsDay, FirstWednesdayOfOctober));
    }

    [Fact]
    public void A_task_restored_after_its_day_waits_for_next_month()
    {
        var restored = Restore(OnFirstWednesday(lastStarted: FirstWednesdayOfSeptember), current: null, new DateTime(2026, 10, 9, 12, 0, 0));

        // Not the next night, when a missed run would catch up, and next month not before its day.
        Assert.False(ManagedScheduleRunner.IsDue(restored, new DateTime(2026, 10, 10, 3, 0, 0)));
        Assert.False(ManagedScheduleRunner.IsDue(restored, new DateTime(2026, 11, 3, 3, 0, 0)));
        Assert.True(ManagedScheduleRunner.IsDue(restored, FirstWednesdayOfNovember));
    }

    [Fact]
    public void A_task_restored_in_a_month_it_already_ran_in_does_not_run_again()
    {
        var restoredOn = new DateTime(2026, 10, 5, 12, 0, 0);

        // The run is in Medic's current record, from the schedule the restore replaces.
        var current = new ManagedRun { Weekday = DayOfWeek.Saturday, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks, LastStartedLocal = FirstSaturdayOfOctober };
        Assert.False(ManagedScheduleRunner.IsDue(Restore(OnFirstWednesday(lastStarted: FirstWednesdayOfSeptember), current, restoredOn), FirstWednesdayOfOctober));

        // Or only in the backup, taken after the run.
        Assert.False(ManagedScheduleRunner.IsDue(Restore(OnFirstWednesday(lastStarted: FirstSaturdayOfOctober), current: null, restoredOn), FirstWednesdayOfOctober));
    }

    // A task Medic runs on the first Wednesday of each month at 03:00.
    private static ManagedRun OnFirstWednesday(DateTime? lastStarted) => new()
    {
        Weekday = DayOfWeek.Wednesday,
        TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        LastStartedLocal = lastStarted
    };

    // The backup's record as RestorePreviousSchedule writes it back.
    private static ManagedRun Restore(ManagedRun backup, ManagedRun? current, DateTime restoredOn)
    {
        backup.LastStartedLocal = ScheduleController.RestoredLastStart(backup, current, restoredOn);
        return backup;
    }

    private static List<CalendarRun> PlannedRuns(FakeTaskWorker task, Dictionary<string, ManagedRun> managed, DateTime date) =>
        ScheduleCalendar.PlannedRuns(new[] { task }, new Dictionary<string, ReliabilityRecord>(), managed, date, date.AddHours(12));
}

using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

/// <summary>Inputs shared by the correlation tests.</summary>
internal static class Dash
{
    public static TimeZoneInfo Zone(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone("UTC" + hours.ToString("+0;-0", System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(hours), "test", "test");

    public static DateTime Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    public static RunRecord Run(string taskId, string name, DateTime startUtc, int minutes = 10, string status = "Completed") => new()
    {
        TaskId = taskId,
        TaskName = name,
        StartUtc = startUtc,
        EndUtc = startUtc.AddMinutes(minutes),
        Status = status
    };
}

public sealed class BusyHoursTests
{
    // 12 October 2026 is a Monday.
    private static readonly DateTime TuesdayNoon = Dash.Utc(2026, 10, 13, 12);

    private static UsageSummary Usage(bool ready, params (int Index, double Streams)[] hours)
    {
        var usage = new UsageSummary { Ready = ready, HoursCollected = ready ? 100 : 10, AverageStreams = Enumerable.Repeat(0d, 168).ToList() };
        foreach (var (index, streams) in hours)
        {
            usage.AverageStreams[index] = streams;
        }

        return usage;
    }

    [Fact]
    public void A_run_counts_in_the_server_local_weekday_and_hour_of_its_start()
    {
        // Monday 23:30 UTC is Tuesday 13:30 at UTC+14, and Monday 20:30 at UTC-3.
        var run = Dash.Run("a", "Scan", Dash.Utc(2026, 10, 12, 23, 30));

        var east = DashboardCorrelations.BusyHours(Usage(true), new UsageProfile(), new[] { run }, TuesdayNoon, Dash.Zone(14));
        var west = DashboardCorrelations.BusyHours(Usage(true), new UsageProfile(), new[] { run }, TuesdayNoon, Dash.Zone(-3));

        Assert.Equal("Scan", Assert.Single(east.Days[1][13].Runs).TaskName);
        Assert.Equal(1, Assert.Single(west.Days[0][20].Runs).Count);
        Assert.Equal(1, east.Days.SelectMany(d => d).Sum(h => h.Runs.Count));
    }

    [Fact]
    public void A_run_just_after_midnight_UTC_belongs_to_the_day_before_west_of_UTC()
    {
        // Tuesday 01:30 UTC is still Monday 22:30 at UTC-3.
        var run = Dash.Run("a", "Scan", Dash.Utc(2026, 10, 13, 1, 30));

        var section = DashboardCorrelations.BusyHours(Usage(true), new UsageProfile(), new[] { run }, TuesdayNoon, Dash.Zone(-3));

        Assert.Single(section.Days[0][22].Runs);
    }

    [Fact]
    public void A_run_saved_without_a_time_zone_kind_counts_as_UTC()
    {
        // A time read back from JSON without a "Z" has no kind; it's still UTC.
        var run = Dash.Run("a", "Scan", new DateTime(2026, 10, 12, 23, 30, 0, DateTimeKind.Unspecified));

        var section = DashboardCorrelations.BusyHours(Usage(true), new UsageProfile(), new[] { run }, TuesdayNoon, Dash.Zone(14));

        Assert.Single(section.Days[1][13].Runs);
    }

    [Fact]
    public void An_hour_is_busy_from_an_average_of_exactly_one_stream()
    {
        // Monday 20:00 averages 1.0 streams, Monday 21:00 averages 0.99.
        var usage = Usage(true, (20, 1.0), (21, 0.99));
        var runs = new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 12, 20, 5)), Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 12, 21, 5)) };

        var section = DashboardCorrelations.BusyHours(usage, new UsageProfile(), runs, TuesdayNoon, Dash.Zone(0));

        Assert.True(section.Days[0][20].Busy);
        Assert.False(section.Days[0][21].Busy);
        Assert.Equal(1, section.RunsInBusyHours);
    }

    [Fact]
    public void No_hour_is_busy_before_the_profile_is_ready()
    {
        var run = Dash.Run("a", "Scan", Dash.Utc(2026, 10, 12, 20, 5));

        var section = DashboardCorrelations.BusyHours(Usage(false, (20, 3.0)), new UsageProfile(), new[] { run }, TuesdayNoon, Dash.Zone(0));

        Assert.False(section.Ready);
        Assert.Equal(10.0, section.HoursRecorded);
        Assert.Equal(3.0, section.Days[0][20].AverageStreams);
        Assert.False(section.Days[0][20].Busy);
        Assert.Equal(0, section.RunsInBusyHours);
    }

    [Fact]
    public void Runs_older_than_a_week_are_left_out_and_transcodes_are_averaged_per_hour()
    {
        var profile = new UsageProfile();
        profile.Buckets[20] = new UsageBucket { Samples = 4, TranscodeSum = 2 };
        var old = Dash.Run("a", "Scan", Dash.Utc(2026, 10, 5, 20, 5));

        var section = DashboardCorrelations.BusyHours(Usage(true), profile, new[] { old }, TuesdayNoon, Dash.Zone(0));

        Assert.Empty(section.Days.SelectMany(d => d).SelectMany(h => h.Runs));
        Assert.Equal(0.5, section.Days[0][20].AverageTranscodes);
        Assert.Equal(7, section.Days.Count);
        Assert.All(section.Days, d => Assert.Equal(24, d.Count));
    }
}

public sealed class TodayTests
{
    // 14 October 2026, 12:00 UTC.
    private static readonly DateTime Noon = Dash.Utc(2026, 10, 14, 12);

    private static CalendarRun Planned(string taskId, string name, int startMinutes) => new()
    {
        TaskId = taskId,
        TaskName = name,
        StartMinutes = startMinutes,
        SortMinutes = startMinutes,
        DurationMinutes = 20,
        Category = "library",
        Kind = "normal"
    };

    private static readonly (string TaskId, string TaskName, DateTime StartUtc)[] NoneRunning = Array.Empty<(string, string, DateTime)>();

    private static TodaySection At(IEnumerable<CalendarRun> planned, IEnumerable<RunRecord> runs) =>
        DashboardCorrelations.Today(Noon, Dash.Zone(0), planned, runs, NoneRunning, Array.Empty<ResourceEvent>(), Array.Empty<LoadGuardEvent>());

    [Fact]
    public void A_run_that_started_more_than_30_minutes_after_its_planned_time_is_late()
    {
        var today = At(
            new[] { Planned("a", "Scan", 180), Planned("b", "Trickplay", 240) },
            new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 45)), Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 14, 4, 20)) });

        Assert.Equal(new[] { "late", "ran" }, today.Planned.Select(p => p.Status));
        Assert.Equal(1, today.Summary.Late);
        Assert.Equal(2, today.Summary.Runs);
    }

    [Fact]
    public void A_planned_run_is_not_run_an_hour_after_its_time_and_waiting_before()
    {
        // At 12:00, 10:00 is two hours past and 11:30 only half an hour.
        var today = At(new[] { Planned("a", "Scan", 600), Planned("b", "Trickplay", 690) }, Array.Empty<RunRecord>());

        Assert.Equal(new[] { "not run", "waiting" }, today.Planned.Select(p => p.Status));
        Assert.Equal(1, today.Summary.NotRun);
    }

    [Fact]
    public void A_run_only_counts_for_its_own_task()
    {
        var today = At(new[] { Planned("a", "Scan", 180) }, new[] { Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 14, 3, 0)) });

        Assert.Equal("not run", Assert.Single(today.Planned).Status);
        Assert.Equal("Trickplay", Assert.Single(today.Actual).TaskName);
    }

    [Fact]
    public void Each_planned_run_of_a_task_takes_its_own_run()
    {
        // Planned at 02:00 and 08:00; it ran at 02:05 only.
        var today = At(new[] { Planned("a", "Refresh", 120), Planned("a", "Refresh", 480) }, new[] { Dash.Run("a", "Refresh", Dash.Utc(2026, 10, 14, 2, 5)) });

        Assert.Equal(new[] { "ran", "not run" }, today.Planned.Select(p => p.Status));
    }

    [Fact]
    public void A_run_that_starts_a_few_minutes_early_counts_for_its_planned_time()
    {
        var today = At(new[] { Planned("a", "Scan", 180) }, new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 2, 50)) });

        Assert.Equal("ran", Assert.Single(today.Planned).Status);
    }

    [Fact]
    public void Two_identical_triggers_make_one_planned_run()
    {
        var today = At(new[] { Planned("a", "Scan", 180), Planned("a", "Scan", 180) }, new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 0)) });

        Assert.Equal("ran", Assert.Single(today.Planned).Status);
    }

    [Fact]
    public void Only_today_counts_and_marks_are_placed_by_server_local_minute()
    {
        // At UTC-3 it's 09:00 on 14 October. A run at 02:00 UTC on the 14th started at 23:00 on the 13th there.
        var runs = new[]
        {
            Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 2, 0)),
            Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 14, 4, 0), status: "Failed")
        };
        var spikes = new[] { new ResourceEvent { TimeUtc = Dash.Utc(2026, 10, 14, 4, 10), Resource = "CPU", Level = "95% of all CPU threads" } };
        var guard = new[] { new LoadGuardEvent { TimestampUtc = Dash.Utc(2026, 10, 14, 4, 12), Action = "stopped", TaskName = "Trickplay" } };

        var today = DashboardCorrelations.Today(Noon, Dash.Zone(-3), Array.Empty<CalendarRun>(), runs, NoneRunning, spikes, guard);

        Assert.Equal("2026-10-14", today.Date);
        Assert.Equal(540, today.NowMinutes);
        var run = Assert.Single(today.Actual);
        Assert.Equal(60, run.StartMinutes);
        Assert.Equal(70, run.EndMinutes);
        Assert.Equal(1, today.Summary.Failed);
        Assert.Equal(70, Assert.Single(today.Spikes).Minute);
        Assert.Equal(72, Assert.Single(today.Guard).Minute);
    }

    [Fact]
    public void Tasks_that_run_many_times_a_day_are_not_planned()
    {
        var today = At(new[] { Planned("a", "Refresh guide", -1) }, Array.Empty<RunRecord>());

        Assert.Empty(today.Planned);
        Assert.Equal(0, today.Summary.Planned);
    }

    [Fact]
    public void A_task_still_running_counts_for_its_planned_time_and_shows_as_running()
    {
        // Planned at 10:00, started at 10:05 and still going at 12:00: its run has no record yet.
        var running = new[] { (TaskId: "a", TaskName: "Scan", StartUtc: Dash.Utc(2026, 10, 14, 10, 5)) };

        var today = DashboardCorrelations.Today(Noon, Dash.Zone(0), new[] { Planned("a", "Scan", 600) }, Array.Empty<RunRecord>(), running, Array.Empty<ResourceEvent>(), Array.Empty<LoadGuardEvent>());

        Assert.Equal("ran", Assert.Single(today.Planned).Status);
        var run = Assert.Single(today.Actual);
        Assert.Equal("Running", run.Status);
        Assert.Equal(605, run.StartMinutes);
        Assert.Equal(720, run.EndMinutes);
        Assert.Equal(1, today.Summary.Running);
        Assert.Equal(1, today.Summary.Runs);
        Assert.Equal(0, today.Summary.NotRun);
    }

    [Fact]
    public void Two_planned_runs_15_minutes_apart_each_take_their_own_run()
    {
        // Planned at 03:00 and 03:15; it ran at 03:00 and at 03:25.
        var today = At(
            new[] { Planned("a", "Scan", 180), Planned("a", "Scan", 195) },
            new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 0)), Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 25)) });

        Assert.Equal(new[] { "ran", "ran" }, today.Planned.Select(p => p.Status));
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Aborted")]
    public void A_planned_run_whose_only_run_was_cancelled_or_aborted_is_cancelled(string status)
    {
        // Stopped five minutes in, by LoadGuard or "Stop running tasks" say, and not run again.
        var today = At(new[] { Planned("a", "Scan", 180) }, new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 0), minutes: 5, status: status) });

        Assert.Equal("cancelled", Assert.Single(today.Planned).Status);
    }

    [Fact]
    public void A_failed_run_still_counts_as_ran()
    {
        var today = At(new[] { Planned("a", "Scan", 180) }, new[] { Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 0), status: "Failed") });

        Assert.Equal("ran", Assert.Single(today.Planned).Status);
    }

    [Fact]
    public void A_rerun_after_a_cancelled_run_takes_its_planned_run_and_is_late_by_its_own_start()
    {
        // Both were cancelled at their planned time. Scan ran again at 03:10, Trickplay only at 04:45.
        var today = At(
            new[] { Planned("a", "Scan", 180), Planned("b", "Trickplay", 240) },
            new[]
            {
                Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 0), minutes: 5, status: "Cancelled"),
                Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 10)),
                Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 14, 4, 0), minutes: 5, status: "Cancelled"),
                Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 14, 4, 45))
            });

        Assert.Equal(new[] { "ran", "late" }, today.Planned.Select(p => p.Status));
    }

    [Fact]
    public void A_cancelled_run_and_its_rerun_count_for_one_planned_run_only()
    {
        // Each task is planned twice, 15 minutes apart. Scan was cancelled at 03:00 and ran again at 03:05;
        // Trickplay was cancelled at 04:00 and didn't run again.
        var today = At(
            new[] { Planned("a", "Scan", 180), Planned("a", "Scan", 195), Planned("b", "Trickplay", 240), Planned("b", "Trickplay", 255) },
            new[]
            {
                Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 0), minutes: 2, status: "Cancelled"),
                Dash.Run("a", "Scan", Dash.Utc(2026, 10, 14, 3, 5)),
                Dash.Run("b", "Trickplay", Dash.Utc(2026, 10, 14, 4, 0), minutes: 2, status: "Cancelled")
            });

        Assert.Equal(new[] { "ran", "not run", "cancelled", "not run" }, today.Planned.Select(p => p.Status));
    }

    [Fact]
    public void A_run_still_going_at_midnight_stays_in_today_from_minute_0_and_counts_for_its_planned_run()
    {
        // At UTC-3 it's 00:10 on 15 October. Scan has been running since 23:50, Refresh ran from 23:30 to 00:05
        // and Cleanup from 00:02 to 00:05; Trickplay ended at 23:40, before midnight.
        var runs = new[]
        {
            Dash.Run("b", "Refresh", Dash.Utc(2026, 10, 15, 2, 30), minutes: 35),
            Dash.Run("c", "Trickplay", Dash.Utc(2026, 10, 15, 2, 0), minutes: 40),
            Dash.Run("d", "Cleanup", Dash.Utc(2026, 10, 15, 3, 2), minutes: 3)
        };
        var running = new[] { (TaskId: "a", TaskName: "Scan", StartUtc: Dash.Utc(2026, 10, 15, 2, 50)) };

        var today = DashboardCorrelations.Today(Dash.Utc(2026, 10, 15, 3, 10), Dash.Zone(-3), new[] { Planned("a", "Scan", 0) }, runs, running, Array.Empty<ResourceEvent>(), Array.Empty<LoadGuardEvent>());

        Assert.Equal("2026-10-15", today.Date);
        Assert.Equal(new[] { "Refresh", "Scan", "Cleanup" }, today.Actual.Select(r => r.TaskName));
        Assert.Equal(new[] { true, true, false }, today.Actual.Select(r => r.StartedBeforeToday));
        Assert.Equal(new[] { 0, 0, 2 }, today.Actual.Select(r => r.StartMinutes));
        Assert.Equal(new[] { 5, 10, 5 }, today.Actual.Select(r => r.EndMinutes));
        Assert.Equal("ran", Assert.Single(today.Planned).Status);
        Assert.Equal(1, today.Summary.Running);
    }

    [Fact]
    public void A_run_from_before_midnight_counts_for_a_planned_run_by_its_real_start()
    {
        // At UTC+14 it's 00:15 on 15 October. Scan has been running since 23:50: 20 minutes before its 00:10
        // planned run, too early to be that run.
        var running = new[] { (TaskId: "a", TaskName: "Scan", StartUtc: Dash.Utc(2026, 10, 14, 9, 50)) };

        var today = DashboardCorrelations.Today(Dash.Utc(2026, 10, 14, 10, 15), Dash.Zone(14), new[] { Planned("a", "Scan", 10) }, Array.Empty<RunRecord>(), running, Array.Empty<ResourceEvent>(), Array.Empty<LoadGuardEvent>());

        Assert.Equal("waiting", Assert.Single(today.Planned).Status);
        Assert.True(Assert.Single(today.Actual).StartedBeforeToday);
    }
}

public sealed class TranscodeReasonsTests
{
    private static TranscodeLogView Log() => new()
    {
        SinceUtc = Dash.Utc(2026, 10, 1, 0),
        Files = 3,
        Samples = 20,
        ReasonTotals = new(StringComparer.Ordinal) { ["AudioCodecNotSupported"] = 10, ["ContainerBitrateExceedsLimit"] = 6, ["SubtitleCodecNotSupported"] = 4 },
        Top = new()
        {
            new TranscodeEntry { Title = "Film A (2020)", Samples = 12, Reasons = new(StringComparer.Ordinal) { ["SubtitleCodecNotSupported"] = 4, ["AudioCodecNotSupported"] = 10 } },
            new TranscodeEntry { Title = "Film B (2021)", Samples = 8, Reasons = new(StringComparer.Ordinal) { ["ContainerBitrateExceedsLimit"] = 6 } }
        }
    };

    [Fact]
    public void Each_reason_is_paired_with_its_cause_in_the_media_report()
    {
        var report = new MediaReportResult { GeneratedUtc = Dash.Utc(2026, 10, 10, 0), Compatibility = new CompatibilityCounts { DtsOrTrueHdOnly = 31, ImageSubtitles = 12 } };

        var section = DashboardCorrelations.Transcodes(Log(), report);

        Assert.Equal(new[] { "AudioCodecNotSupported", "ContainerBitrateExceedsLimit", "SubtitleCodecNotSupported" }, section.Reasons.Select(r => r.Reason));
        Assert.Equal(new[] { 50.0, 30.0, 20.0 }, section.Reasons.Select(r => r.Share));
        Assert.Equal(new string?[] { "DtsOrTrueHdOnly", null, "ImageSubtitles" }, section.Reasons.Select(r => r.LibraryCause));
        Assert.Equal(new int?[] { 31, null, 12 }, section.Reasons.Select(r => r.LibraryCount));
        Assert.Equal(report.GeneratedUtc, section.MediaReportUtc);
    }

    [Fact]
    public void Without_a_media_report_the_causes_are_named_but_not_counted()
    {
        var section = DashboardCorrelations.Transcodes(Log(), null);

        Assert.Null(section.MediaReportUtc);
        Assert.Equal("DtsOrTrueHdOnly", section.Reasons[0].LibraryCause);
        Assert.All(section.Reasons, r => Assert.Null(r.LibraryCount));
    }

    [Fact]
    public void The_top_titles_list_their_reasons_most_frequent_first()
    {
        var section = DashboardCorrelations.Transcodes(Log(), null);

        Assert.Equal(20, section.Samples);
        Assert.Equal(new[] { "Film A (2020)", "Film B (2021)" }, section.Top.Select(t => t.Title));
        Assert.Equal(new[] { "AudioCodecNotSupported", "SubtitleCodecNotSupported" }, section.Top[0].Reasons);
    }
}

public sealed class DiskOutlookTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 10, 14);

    private readonly string _folder = Directory.CreateTempSubdirectory("medic-disks-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static DiskSample Reading(string key, string day, double freeGb, string holds = "") =>
        new() { Key = key, Day = day, FreeGb = freeGb, Holds = holds, Path = key + "/media" };

    private DiskSample[] ReadThroughFolder() => new[]
    {
        new DiskSample { Key = "/mnt/usb", Day = "2026-10-13", FreeGb = 300, Holds = "Films", Path = _folder },
        new DiskSample { Key = "/mnt/usb", Day = "2026-10-14", FreeGb = 290, Holds = "Films", Path = _folder }
    };

    private static (double FreeGb, double TotalGb)? Unreadable(string path) => null;

    private static bool Present(string path) => true;

    [Fact]
    public void A_drive_with_under_3_days_of_readings_has_no_rate_yet()
    {
        var history = new[] { Reading("/mnt/a", "2026-10-13", 500), Reading("/mnt/a", "2026-10-14", 498) };

        var drive = Assert.Single(DashboardCorrelations.Disks(history, _ => (480.0, 1000.0), Array.Empty<FreedDay>(), Today, Present).Drives);

        Assert.Null(drive.GbPerDay);
        Assert.Null(drive.DaysToFull);
        Assert.Equal(480.0, drive.FreeGb);
        Assert.Equal(1000.0, drive.TotalGb);
        Assert.Equal(2, drive.Series.Count);
    }

    [Fact]
    public void A_filling_drive_gets_its_rate_and_days_to_full_from_its_readings()
    {
        // 30 GB used over 10 days is 3 GB a day, so 300 GB free now lasts 100 days.
        var history = new[] { Reading("/mnt/a", "2026-10-04", 330, "Films"), Reading("/mnt/a", "2026-10-14", 300) };

        var drive = Assert.Single(DashboardCorrelations.Disks(history, _ => (300.0, 2000.0), Array.Empty<FreedDay>(), Today, Present).Drives);

        Assert.Equal(3.0, drive.GbPerDay);
        Assert.Equal(100.0, drive.DaysToFull);
        Assert.Equal("Films", drive.Holds);
    }

    [Fact]
    public void A_drive_that_cant_be_read_now_shows_its_last_reading()
    {
        var history = new[] { Reading("/mnt/usb", "2026-10-14", 120, "Backups") };

        var drive = Assert.Single(DashboardCorrelations.Disks(history, Unreadable, Array.Empty<FreedDay>(), Today, Present).Drives);

        Assert.Equal(120.0, drive.FreeGb);
        Assert.Equal(0.0, drive.TotalGb);
        Assert.Equal("Backups", drive.Holds);
    }

    [Fact]
    public void Drives_come_soonest_full_first_and_freed_days_oldest_first()
    {
        var history = new[]
        {
            Reading("/mnt/a", "2026-10-04", 100), Reading("/mnt/a", "2026-10-14", 100),
            Reading("/mnt/b", "2026-10-04", 60), Reading("/mnt/b", "2026-10-14", 50)
        };
        var freed = new[] { new FreedDay { Day = "2026-10-12", Bytes = 5 }, new FreedDay { Day = "2026-10-03", Bytes = 7 } };

        var section = DashboardCorrelations.Disks(history, path => path.StartsWith("/mnt/b", StringComparison.Ordinal) ? (50.0, 100.0) : (100.0, 100.0), freed, Today, Present);

        Assert.Equal(new[] { "/mnt/b", "/mnt/a" }, section.Drives.Select(d => d.Key));
        Assert.Equal(new[] { "2026-10-03", "2026-10-12" }, section.Freed.Select(f => f.Day));
    }

    [Fact]
    public void A_drive_not_read_for_a_week_is_left_out()
    {
        // A drive that's gone, or a key no longer used, keeps its old readings for 30 days.
        var history = new[] { Reading("/mnt/a", "2026-10-14", 100), Reading("931.5/500.0", "2026-10-06", 500) };

        var section = DashboardCorrelations.Disks(history, _ => (100.0, 200.0), Array.Empty<FreedDay>(), Today, Present);

        Assert.Equal("/mnt/a", Assert.Single(section.Drives).Key);
    }

    [Fact]
    public void A_library_drive_whose_folder_is_gone_is_missing_not_read_through_its_parent()
    {
        var history = new[] { Reading("/mnt/usb", "2026-10-04", 300, "Films"), Reading("/mnt/usb", "2026-10-14", 200) };

        var drive = Assert.Single(DashboardCorrelations.Disks(history, _ => (40.0, 100.0), Array.Empty<FreedDay>(), Today, _ => false).Drives);

        Assert.True(drive.Missing);
        Assert.Equal("/mnt/usb/media", drive.Path);
        Assert.Equal(200.0, drive.FreeGb);
        Assert.Equal(0.0, drive.TotalGb);
        Assert.Null(drive.GbPerDay);
        Assert.Null(drive.DaysToFull);
    }

    [Fact]
    public void An_empty_library_folder_is_missing_and_shows_no_live_space()
    {
        // A drive that didn't mount often leaves its mount point behind, empty, on the drive above it.
        var drive = Assert.Single(DashboardCorrelations.Disks(ReadThroughFolder(), _ => (40.0, 100.0), Array.Empty<FreedDay>(), Today).Drives);

        Assert.True(drive.Missing);
        Assert.Equal(_folder, drive.Path);
        Assert.Equal(290.0, drive.FreeGb);
        Assert.Equal(0.0, drive.TotalGb);
        Assert.Null(drive.GbPerDay);
    }

    [Fact]
    public void A_library_folder_with_entries_is_read()
    {
        File.WriteAllText(Path.Combine(_folder, "Film (2020).mkv"), string.Empty);

        var drive = Assert.Single(DashboardCorrelations.Disks(ReadThroughFolder(), _ => (280.0, 1000.0), Array.Empty<FreedDay>(), Today).Drives);

        Assert.False(drive.Missing);
        Assert.Equal(280.0, drive.FreeGb);
        Assert.Equal(1000.0, drive.TotalGb);
    }

    [Fact]
    public void A_transcode_folder_not_made_yet_is_not_missing()
    {
        var history = new[] { Reading("/", "2026-10-14", 50, "transcodes, Films") };

        var drive = Assert.Single(DashboardCorrelations.Disks(history, _ => (40.0, 100.0), Array.Empty<FreedDay>(), Today, _ => false).Drives);

        Assert.False(drive.Missing);
        Assert.Equal(40.0, drive.FreeGb);
    }
}

public sealed class RemoteStreamingTests
{
    private static SpeedTestResult Test(double uploadMbps) =>
        new() { TestedUtc = Dash.Utc(2026, 10, 10, 8), DownloadMbps = 500, UploadMbps = uploadMbps, LatencyMs = 12, IptvHdStreams = 50 };

    [Fact]
    public void Without_a_speed_test_only_the_limit_and_the_streams_are_known()
    {
        var section = DashboardCorrelations.Network(null, 8_000_000, 3);

        Assert.Null(section.Test);
        Assert.Null(section.Suggested);
        Assert.Equal(8.0, section.CurrentLimitMbps);
        Assert.Equal(3, section.MostStreams);
    }

    [Fact]
    public void The_suggestion_is_the_speed_tests_option_for_the_most_streams_seen()
    {
        // 50 Mbps up, 80% of it shared by 4 people: 10 Mbps each.
        var section = DashboardCorrelations.Network(Test(50), 0, 4);

        Assert.Equal(4, section.Suggested!.Viewers);
        Assert.Equal(10, section.Suggested.RecommendedLimitMbps);
        Assert.Equal(0.0, section.CurrentLimitMbps);
        Assert.Equal(50.0, section.Test!.UploadMbps);
    }

    [Fact]
    public void More_viewers_than_the_options_cover_use_the_largest_and_none_yet_the_smallest()
    {
        Assert.Equal(6, DashboardCorrelations.Network(Test(50), 0, 9).Suggested!.Viewers);
        Assert.Equal(1, DashboardCorrelations.Network(Test(50), 0, 0).Suggested!.Viewers);
    }

    [Fact]
    public void A_test_that_measured_no_upload_suggests_nothing()
    {
        Assert.Null(DashboardCorrelations.Network(Test(0), 0, 2).Suggested);
    }
}

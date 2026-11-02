using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Events;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMedic.Api;
using JellyfinMedic.Services;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyfinMedic.Tests;

public sealed class ScheduleCalendarTests
{
    [Fact]
    public void A_day_has_each_daily_run_and_that_weekdays_weekly_runs_in_time_order()
    {
        var scan = new FakeTaskWorker("Scan media library", "RefreshLibrary", 30, FakeTaskWorker.Daily(3, 0));
        var people = new FakeTaskWorker("Refresh people", "RefreshPeople", 10, FakeTaskWorker.Weekly(DayOfWeek.Tuesday, 2, 0));
        var chapters = new FakeTaskWorker("Extract chapter images", "RefreshChapterImages", 10, FakeTaskWorker.Weekly(DayOfWeek.Monday, 1, 0));
        var tuesday = new DateTime(2026, 10, 13);

        var runs = ScheduleCalendar.PlannedRuns(new[] { scan, people, chapters }, new Dictionary<string, ReliabilityRecord>(), new Dictionary<string, ManagedRun>(), tuesday, tuesday.AddHours(12));

        Assert.Equal(new[] { "Refresh people", "Scan media library" }, runs.Select(r => r.TaskName));
        Assert.Equal(new[] { 120, 180 }, runs.Select(r => r.StartMinutes));
    }
}

public sealed class ResourceReadingsTests
{
    [Fact]
    public void The_buffer_keeps_the_last_40_readings_oldest_first_and_hands_out_a_copy()
    {
        var start = Dash.Utc(2026, 10, 14, 12);
        for (int i = 0; i < 41; i++)
        {
            ResourceMonitor.Remember(new ResourceSample { TimeUtc = start.AddSeconds(15 * i), CpuPercent = i });
        }

        ResourceMonitor.RecentReadings().Clear();
        var readings = ResourceMonitor.RecentReadings();

        Assert.Equal(40, readings.Count);
        Assert.Equal(1.0, readings[0].CpuPercent);
        Assert.Equal(40.0, readings[^1].CpuPercent);
    }
}

public sealed class LoadGuardPausedTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void A_stopped_task_is_listed_once_with_its_name_until_it_is_taken_off()
    {
        using var guard = new LoadGuard(new FakeTaskManager(), _paths, NullLogger<LoadGuard>.Instance);
        var scan = new FakeTaskWorker("Scan media library", "RefreshLibrary", 30);

        guard.Defer(scan);
        guard.Defer(scan);

        var paused = Assert.Single(LoadGuard.Paused);
        Assert.Equal("Scan media library", paused.TaskName);
        Assert.Equal(scan.Id, paused.TaskId);
        guard.DropFirst();
        Assert.Empty(LoadGuard.Paused);
    }

    [Fact]
    public async Task The_list_survives_a_restart_of_Jellyfin()
    {
        var scan = new FakeTaskWorker("Scan media library", "RefreshLibrary", 30);
        using (var before = new LoadGuard(new FakeTaskManager(), _paths, NullLogger<LoadGuard>.Instance))
        {
            before.Defer(scan);
        }

        using var after = new LoadGuard(new FakeTaskManager(), _paths, NullLogger<LoadGuard>.Instance);
        await after.StartAsync(CancellationToken.None);
        await after.StopAsync(CancellationToken.None);

        Assert.Equal(scan.Id, Assert.Single(LoadGuard.Paused).TaskId);
        after.DropFirst();
    }

    [Fact]
    public void After_a_restart_each_task_comes_back_once_and_none_stopped_over_a_day_ago()
    {
        var now = Dash.Utc(2026, 10, 17, 9);
        var saved = new[]
        {
            new PausedTask { TaskId = "b", TaskName = "Trickplay", StoppedUtc = now.AddHours(-2) },
            new PausedTask { TaskId = "a", TaskName = "Scan", StoppedUtc = now.AddHours(-3) },
            new PausedTask { TaskId = "a", TaskName = "Scan", StoppedUtc = now.AddHours(-1) },
            new PausedTask { TaskId = "c", TaskName = "Old", StoppedUtc = now.AddHours(-25) }
        };

        Assert.Equal(new[] { "a", "b" }, LoadGuard.Restore(saved, now).Select(p => p.TaskId));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}

public sealed class SafeRestartTests
{
    [Theory]
    [InlineData("RefreshLibrary", true)]
    [InlineData(RestartForUpdatesTask.TaskKey, false)]
    [InlineData(ScheduledRestartTask.TaskKey, false)]
    public void A_restart_waits_for_other_tasks_but_not_for_either_restart_task(string running, bool blocks)
    {
        Assert.Equal(blocks, SafeRestart.Blocks(running, ScheduledRestartTask.TaskKey));
        Assert.Equal(blocks, SafeRestart.Blocks(running, RestartForUpdatesTask.TaskKey));
    }
}

public sealed class DiskReadingsTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void A_reading_records_what_the_drive_holds_and_a_later_one_the_same_day_fills_it_in()
    {
        string dir = _paths.PluginConfigurationsPath;
        StorageWatch.Record(dir, new List<DriveStatus> { new() { Key = "/mnt/a", FreeGb = 100, TotalGb = 200, ExamplePath = _paths.Root } });
        StorageWatch.Record(dir, new List<DriveStatus> { new() { Key = "/mnt/a", Holds = "Films", FreeGb = 90, TotalGb = 200, ExamplePath = _paths.Root } });

        var reading = Assert.Single(StorageWatch.History(dir));
        Assert.Equal("Films", reading.Holds);
        Assert.Equal(100.0, reading.FreeGb);
    }

    [Fact]
    public void The_background_reading_carries_forward_what_the_drive_holds()
    {
        string dir = _paths.PluginConfigurationsPath;
        string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(dir, "JellyfinMedic"));
        File.WriteAllText(
            Path.Combine(dir, "JellyfinMedic", "disk_history.json"),
            JsonSerializer.Serialize(new[] { new DiskSample { Key = "/mnt/a", Day = yesterday, FreeGb = 50, Holds = "Films", Path = _paths.Root } }));

        StorageWatch.RecordKnownNow(dir);

        Assert.Equal("Films", StorageWatch.History(dir).Single(h => h.Day != yesterday).Holds);
    }

[Fact]
    public void Readings_kept_under_keys_that_moved_with_free_space_come_back_under_the_drives_own_key()
    {
        // Where mount points couldn't be read, a drive's key was its size and free space, so it changed every day.
        string dir = _paths.PluginConfigurationsPath;
        string DaysAgo(int days) => DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(dir, "JellyfinMedic"));
        File.WriteAllText(
            Path.Combine(dir, "JellyfinMedic", "disk_history.json"),
            JsonSerializer.Serialize(new[]
            {
                new DiskSample { Key = "931.5/500.0", Day = DaysAgo(2), FreeGb = 500, Holds = "Films", Path = _paths.Root },
                new DiskSample { Key = "931.5/499.0", Day = DaysAgo(1), FreeGb = 499, Holds = "Films", Path = _paths.Root }
            }));

        StorageWatch.RecordKnownNow(dir);

        var reading = Assert.Single(StorageWatch.History(dir), h => h.Day == DaysAgo(0));
        Assert.Equal(StorageWatch.Drives(new[] { ("Films", _paths.Root) }).Single().Key, reading.Key);
        Assert.Equal("Films", reading.Holds);
    }

        public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}

public sealed class FreedDaysTests
{
    [Fact]
    public void Space_freed_the_same_day_adds_up_with_each_kind_named_once()
    {
        long before = WeeklyLedger.FreedDays(DateTime.Today).Sum(d => d.Bytes);

        WeeklyLedger.AddFreed("Clean-ups", 1000);
        WeeklyLedger.AddFreed("Track cleanup", 500);
        WeeklyLedger.AddFreed("Clean-ups", 250);

        var today = Assert.Single(WeeklyLedger.FreedDays(DateTime.Today));
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.Day);
        Assert.Equal(before + 1750, today.Bytes);
        Assert.Equal(new[] { "Clean-ups", "Track cleanup" }, today.Kinds.Order(StringComparer.Ordinal));
    }
}

public sealed class SignInTests
{
    // An activity log entry as the auditor reads it, by property name.
    private sealed class Entry
    {
        public string Type { get; init; } = "AuthenticationSucceeded";

        public string Name { get; init; } = "roamer signed in";

        public string ShortOverview { get; init; } = string.Empty;

        public string UserId { get; init; } = string.Empty;
    }

    // Jellyfin's activity log, answering as Jellyfin 12.2 does: from MinDate, newest first, Skip then Limit (100 when unset).
    private sealed class FakeActivityLog : IActivityManager
    {
        private readonly List<ActivityLogEntry> _entries;
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public FakeActivityLog(IEnumerable<ActivityLogEntry> entries) => _entries = entries.ToList();

        // Every reading fails with this when it's set.
        public Exception? Failure { get; init; }

        // Something new is logged after each page is read, as on a busy server, so older entries move down a place.
        public bool LogsWhileRead { get; init; }

        // Answers wait for Release, as a busy database's would.
        public bool Held { get; init; }

        // How many pages were asked for.
        public int Calls => Volatile.Read(ref _calls);

        public event EventHandler<GenericEventArgs<ActivityLogEntry>> EntryCreated
        {
            add { }
            remove { }
        }

        public void Release() => _released.TrySetResult();

        // Public rather than an explicit interface member: Medic looks the method up on the object's own type.
        public Task<QueryResult<ActivityLogEntry>> GetPagedResultAsync(ActivityLogQuery query)
        {
            Interlocked.Increment(ref _calls);
            if (Failure is not null)
            {
                return Task.FromException<QueryResult<ActivityLogEntry>>(Failure);
            }

            var matching = _entries.Where(e => query.MinDate is null || e.Date >= query.MinDate).OrderByDescending(e => e.Date).ToList();
            var page = matching.Skip(query.Skip ?? 0).Take(query.Limit ?? 100).ToList();
            if (LogsWhileRead)
            {
                _entries.Add(Playback(_entries.Max(e => e.Id) + 1, _entries.Max(e => e.Date).AddSeconds(1)));
            }

            var result = new QueryResult<ActivityLogEntry>(query.Skip, matching.Count, page);
            return Held ? AfterRelease(result) : Task.FromResult(result);
        }

        public Task CreateAsync(ActivityLog entry) => throw new NotSupportedException();

        public Task CleanAsync(DateTime startDate) => throw new NotSupportedException();

        private async Task<QueryResult<ActivityLogEntry>> AfterRelease(QueryResult<ActivityLogEntry> result)
        {
            await _released.Task;
            return result;
        }
    }

    // A clock the test moves by hand. WaitAsync's time limit runs on its timers, which go off once it passes their time.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.Due <= _now).ToList();
                _timers.RemoveAll(t => t.Due <= _now);
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        // Goes off once: WaitAsync never asks for a repeating timer.
        private sealed class ManualTimer : ITimer
        {
            private readonly ManualClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_clock._gate)
                {
                    _clock._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = _clock._now + dueTime;
                        _clock._timers.Add(this);
                    }
                }

                return true;
            }

            public void Fire() => _callback(_state);

            public void Dispose()
            {
                lock (_clock._gate)
                {
                    _clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static ActivityLogEntry Playback(long id, DateTime date) =>
        new("roamer is playing a film", "VideoPlayback", Guid.Empty) { Id = id, Date = date };

    private static ActivityLogEntry FailedSignIn(long id, DateTime date) =>
        new("Failed sign-in from roamer", "AuthenticationFailed", Guid.Empty) { Id = id, Date = date, ShortOverview = "IP address: 198.51.100.7" };

    // The checks as the Report runs them, with no accounts; the library check has no user manager and is skipped.
    private static Task<List<Finding>> ReportAsync(object? log) =>
        SecurityAuditor.AuditAsync(log, null!, new List<object>(), CancellationToken.None);

    // The roaming list as the Dashboard reads it, with no accounts.
    private static Task<List<RoamingAccount>?> DashboardAsync(object? log) =>
        SecurityAuditor.RoamingAccountsAsync(log, new List<object>(), CancellationToken.None);

    [Fact]
    public void An_account_is_counted_by_network_and_failed_sign_ins_apart()
    {
        string id = Guid.NewGuid().ToString("N");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [id] = "roamer" };
        var entries = new List<object>
        {
            new Entry { UserId = id, ShortOverview = "IP address: 203.0.113.10" },
            new Entry { UserId = id, ShortOverview = "IP address: 203.0.113.11" },
            new Entry { UserId = id, ShortOverview = "IP address: 2001:db8:1:2::10" },
            new Entry { UserId = id, ShortOverview = "IP address: 2001:db8:1:2::99" },
            new Entry { Type = "AuthenticationFailed", Name = "Failed sign-in", ShortOverview = "IP address: 198.51.100.7" }
        };

        var (failures, networks) = SecurityAuditor.SignIns(entries, names);

        Assert.Equal(3, networks["roamer"].Count);
        Assert.Equal("198.51.100.7", Assert.Single(failures).Ip);
    }

    [Fact]
    public void Every_address_inside_the_home_network_counts_as_one_place()
    {
        // A TV, a phone and a laptop at home, and the same person over Tailscale, are one place; a public address is another.
        string id = Guid.NewGuid().ToString("N");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [id] = "family" };
        var entries = new List<object>
        {
            new Entry { UserId = id, ShortOverview = "IP address: 192.168.1.10" },
            new Entry { UserId = id, ShortOverview = "IP address: 192.168.1.11" },
            new Entry { UserId = id, ShortOverview = "IP address: 192.168.1.12" },
            new Entry { UserId = id, ShortOverview = "IP address: 100.64.1.1" },
            new Entry { UserId = id, ShortOverview = "IP address: 203.0.113.10" }
        };

        var (_, networks) = SecurityAuditor.SignIns(entries, names);

        Assert.Equal(2, networks["family"].Count);
    }

    [Fact]
    public async Task Without_an_activity_log_sign_ins_couldnt_be_checked()
    {
        Assert.Null(await DashboardAsync(null));
        var finding = Assert.Single(await ReportAsync(null));
        Assert.Equal("Sign-ins couldn't be checked this time", finding.Title);
        Assert.Equal("Jellyfin's activity log isn't available to Medic", finding.Current);
    }

    [Fact]
    public async Task A_reading_that_fails_says_sign_ins_couldnt_be_checked()
    {
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>()) { Failure = new InvalidOperationException("database is locked") };

        Assert.Null(await DashboardAsync(log));
        var finding = Assert.Single(await ReportAsync(log));
        Assert.Equal("Sign-ins couldn't be checked this time", finding.Title);
        Assert.Equal("Reading the activity log failed (InvalidOperationException)", finding.Current);
    }

    [Fact]
    public async Task Failed_sign_ins_behind_the_newest_500_entries_are_found()
    {
        var now = DateTime.UtcNow;
        var log = new FakeActivityLog(
            Enumerable.Range(1, 520).Select(i => Playback(i, now.AddMinutes(-i)))
                .Concat(Enumerable.Range(1, 12).Select(i => FailedSignIn(1000 + i, now.AddDays(-3).AddMinutes(-i)))));

        var finding = Assert.Single(await ReportAsync(log));

        Assert.Equal("A lot of failed sign-ins", finding.Title);
        Assert.Equal("12 failed sign-ins in the last 7 days; most from 198.51.100.7 (12)", finding.Current);
    }

    [Fact]
    public async Task An_entry_pushed_onto_the_next_page_while_reading_counts_once()
    {
        // The first page ends with a failed sign-in; what's logged after that page pushes it onto the second as well.
        var now = DateTime.UtcNow;
        var log = new FakeActivityLog(
            Enumerable.Range(1, 499).Select(i => Playback(i, now.AddMinutes(-i)))
                .Concat(Enumerable.Range(1, 12).Select(i => FailedSignIn(1000 + i, now.AddHours(-12).AddMinutes(-i)))))
        {
            LogsWhileRead = true
        };

        var finding = Assert.Single(await ReportAsync(log));

        Assert.Equal("12 failed sign-ins in the last 7 days; most from 198.51.100.7 (12)", finding.Current);
    }

    [Fact]
    public async Task A_capped_week_with_failed_sign_ins_says_so_on_that_finding_and_adds_no_tip()
    {
        var now = DateTime.UtcNow;
        var log = new FakeActivityLog(
            Enumerable.Range(1, 12).Select(i => FailedSignIn(i, now.AddMinutes(-i)))
                .Concat(Enumerable.Range(1, 10_000).Select(i => Playback(100 + i, now.AddHours(-1).AddSeconds(-i)))));

        // Single: the failed sign-ins carry the note, so there's no Tip saying it again.
        var finding = Assert.Single(await ReportAsync(log));

        Assert.Equal(
            $"12 failed sign-ins in the last 7 days; most from 198.51.100.7 (12); only the newest {10_000:N0} entries of the activity log were read",
            finding.Current);
    }

    [Fact]
    public async Task A_capped_week_with_nothing_found_says_older_sign_ins_werent_checked()
    {
        // Two failed sign-ins aren't a finding, so nothing else would say the oldest part of the week went unread.
        var now = DateTime.UtcNow;
        var log = new FakeActivityLog(
            Enumerable.Range(1, 2).Select(i => FailedSignIn(i, now.AddMinutes(-i)))
                .Concat(Enumerable.Range(1, 10_000).Select(i => Playback(100 + i, now.AddHours(-1).AddSeconds(-i)))));

        var finding = Assert.Single(await ReportAsync(log));

        Assert.Equal(Sev.Tip, finding.Severity);
        Assert.Equal("Sign-ins were only partly checked", finding.Title);
        Assert.Equal(
            $"Only the newest {10_000:N0} entries of the activity log were read, so older sign-ins in the week weren't checked",
            finding.Current);
    }

    [Fact]
    public async Task Reading_the_log_returns_at_once_and_finishes_when_the_log_answers()
    {
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>()) { Held = true };

        var roaming = DashboardAsync(log);
        Assert.False(roaming.IsCompleted);

        log.Release();
        Assert.Empty((await roaming)!);
    }

    [Fact]
    public async Task A_reading_under_way_is_shared_by_the_report_and_the_dashboard()
    {
        // A page visit asks for both at once.
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>()) { Held = true };

        var dashboard = DashboardAsync(log);
        var report = ReportAsync(log);
        log.Release();
        await Task.WhenAll(dashboard, report);

        Assert.Equal(1, log.Calls);
    }

    [Fact]
    public async Task The_dashboard_reuses_a_good_reading_for_a_minute()
    {
        var clock = new ManualClock();
        var reader = new SecurityAuditor.ActivityLogReader(clock);
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>());

        await reader.ReadAsync(log, reuse: true, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(59));
        await reader.ReadAsync(log, reuse: true, CancellationToken.None);
        Assert.Equal(1, log.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        await reader.ReadAsync(log, reuse: true, CancellationToken.None);
        Assert.Equal(2, log.Calls);
    }

    [Fact]
    public async Task The_report_reads_afresh_and_the_dashboard_uses_its_reading()
    {
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>());

        await ReportAsync(log);
        await DashboardAsync(log);
        Assert.Equal(1, log.Calls);

        await ReportAsync(log);
        Assert.Equal(2, log.Calls);
    }

    [Fact]
    public async Task A_failed_reading_is_not_reused()
    {
        // "Open Medic again later" has to mean a new reading, even within the minute.
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>()) { Failure = new InvalidOperationException("database is locked") };

        Assert.Null(await DashboardAsync(log));
        Assert.Null(await DashboardAsync(log));

        Assert.Equal(2, log.Calls);
    }

    [Fact]
    public async Task A_log_that_doesnt_answer_within_15_seconds_is_reported()
    {
        var clock = new ManualClock();
        var reader = new SecurityAuditor.ActivityLogReader(clock);
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>()) { Held = true };

        var reading = reader.ReadAsync(log, reuse: true, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(reading.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        // Bounded so that a time limit that no longer works fails here instead of hanging the run; never reached otherwise.
        var result = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Entries);
        Assert.Equal("The activity log didn't answer within 15 seconds", result.Reason);
    }

    [Fact]
    public async Task A_caller_that_gives_up_leaves_the_reading_to_the_others()
    {
        var reader = new SecurityAuditor.ActivityLogReader(new ManualClock());
        var log = new FakeActivityLog(Array.Empty<ActivityLogEntry>()) { Held = true };
        using var leaving = new CancellationTokenSource();

        var gaveUp = reader.ReadAsync(log, reuse: true, leaving.Token);
        var waits = reader.ReadAsync(log, reuse: true, CancellationToken.None);
        leaving.Cancel();
        Assert.True(gaveUp.IsCanceled);

        log.Release();
        Assert.NotNull((await waits).Entries);
        Assert.Equal(1, log.Calls);
    }
}

public sealed class DashboardBuilderTests
{
    [Fact]
    public void A_section_that_fails_is_left_out_with_a_message_and_the_rest_still_arrive()
    {
        var data = new DashboardData();

        DashboardBuilder.Section(data, "Disks", NullLogger.Instance, () => throw new IOException("disk_history.json is locked"));
        DashboardBuilder.Section(data, "Network", NullLogger.Instance, () => data.Network = new NetworkSection { MostStreams = 2 });

        Assert.Null(data.Disks);
        Assert.Equal(new[] { "Disks" }, data.Errors.Keys);
        Assert.Contains("IOException", data.Errors["Disks"], StringComparison.Ordinal);
        Assert.Equal(2, data.Network!.MostStreams);
    }
}

public sealed class DriveKeyTests
{
    [Theory]
    [InlineData("/Volumes/Media/Films", "/Volumes/Media")]
    [InlineData("/Volumes/MediaBackup/Films", "/")]
    [InlineData("D:\\Media\\Films", "D:\\")]
    [InlineData("E:\\Films", null)]
    public void A_drive_whose_mount_point_cant_be_read_is_known_by_the_volume_that_holds_it(string path, string? root)
    {
        Assert.Equal(root, StorageWatch.VolumeRoot(path, new[] { "/", "/Volumes/Media", "C:\\", "D:\\" }));
    }
}

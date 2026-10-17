using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class RunHistoryTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly FakeApplicationPaths _paths = new();

    private static RunRecord Run(string task, int minutes) =>
        new() { TaskId = task, TaskName = task, StartUtc = Start.AddMinutes(minutes), EndUtc = Start.AddMinutes(minutes + 1), Status = "Completed" };

    [Fact]
    public void A_task_that_runs_every_few_minutes_cant_push_out_the_others()
    {
        var runs = Enumerable.Range(0, 2000).Select(i => Run("notifier", 10 + i * 5)).Prepend(Run("scan", 0)).ToList();

        var kept = ScheduleStorage.Trim(runs, runs[^1].EndUtc);

        Assert.Equal(2000, kept.Count(r => r.TaskId == "notifier"));
        Assert.Single(kept, r => r.TaskId == "scan");
        // The newest of the frequent task's runs are the ones kept, oldest first.
        Assert.Equal(runs[^1].EndUtc, kept[^1].EndUtc);
        Assert.True(kept.Zip(kept.Skip(1)).All(p => p.First.EndUtc <= p.Second.EndUtc));
    }

    [Fact]
    public void A_task_that_runs_every_5_minutes_keeps_the_week_busy_hours_reads()
    {
        // Eight days of runs every 5 minutes.
        var runs = Enumerable.Range(0, 8 * 288).Select(i => Run("notifier", i * 5)).ToList();
        var now = runs[^1].EndUtc;

        var kept = ScheduleStorage.Trim(runs, now);

        // Busy hours counts the runs that started in the last 7 days (DashboardCorrelations.BusyHours).
        var week = runs.Where(r => r.StartUtc >= now.AddDays(-7)).ToList();
        Assert.Equal(week.Select(r => r.StartUtc), kept.Where(r => r.StartUtc >= now.AddDays(-7)).Select(r => r.StartUtc));
    }

    [Fact]
    public void A_task_that_runs_rarely_keeps_its_newest_runs_past_the_week()
    {
        var runs = Enumerable.Range(0, 400).Select(i => Run("backup", i * 24 * 60)).ToList();

        var kept = ScheduleStorage.Trim(runs, runs[^1].EndUtc);

        Assert.Equal(ScheduleStorage.KeptRunsPerTask, kept.Count);
        Assert.Equal(runs[^1].EndUtc, kept[^1].EndUtc);
    }

    [Fact]
    public void The_history_stays_bounded()
    {
        // A task every minute for 8 days is more than the cap of one task.
        var everyMinute = Enumerable.Range(0, 8 * 24 * 60).Select(i => Run("chatty", i)).ToList();
        var now = everyMinute[^1].EndUtc;
        Assert.Equal(ScheduleStorage.MaxRunsPerTask, ScheduleStorage.Trim(everyMinute, now).Count);

        // Ten tasks every 5 minutes for 8 days, and forty rare tasks with old runs, are more than the cap of all.
        var many = Enumerable.Range(0, 10).SelectMany(t => Enumerable.Range(0, 8 * 288).Select(i => Run("frequent" + t, i * 5)))
            .Concat(Enumerable.Range(0, 40).SelectMany(t => Enumerable.Range(0, 300).Select(i => Run("rare" + t, -i * 60))))
            .ToList();
        var kept = ScheduleStorage.Trim(many, now);

        Assert.Equal(ScheduleStorage.MaxRunHistory, kept.Count);
        // The oldest runs go first.
        Assert.Equal(many.OrderByDescending(r => r.EndUtc).Take(ScheduleStorage.MaxRunHistory).Min(r => r.EndUtc), kept[0].EndUtc);
    }

    [Fact]
    public void A_run_not_written_yet_is_read_back_and_written_with_the_next_flush()
    {
        ScheduleStorage.AppendRun(_paths, Run("scan", 0));
        ScheduleStorage.AppendRun(_paths, Run("trickplay", 5));

        Assert.Equal(2, ScheduleStorage.ReadRuns(_paths).Count);
        ScheduleStorage.FlushRuns(_paths);
        Assert.Equal(new[] { "scan", "trickplay" }, ScheduleStorage.ReadList<RunRecord>(ScheduleStorage.RunHistoryPath(_paths)).Select(r => r.TaskId));
        Assert.Equal(2, ScheduleStorage.ReadRuns(_paths).Count);
    }

    public void Dispose()
    {
        ScheduleStorage.FlushRuns(_paths);
        Directory.Delete(_paths.Root, recursive: true);
    }
}

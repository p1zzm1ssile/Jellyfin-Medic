using JellyfinMedic.Api;
using JellyfinMedic.Services;
using MediaBrowser.Model.Tasks;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

public class UntimedTasksTests
{
    private static readonly Dictionary<string, ReliabilityRecord> NoHistory = new();
    private static readonly Dictionary<string, ManagedRun> NoManaged = new();

    [Fact]
    public void Only_tasks_with_no_time_to_run_at_are_listed()
    {
        var timed = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
        var interval = new FakeTaskWorker("Every hour", "EveryHour", 1, new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks });
        var startup = new FakeTaskWorker("At startup", "AtStartup", 1, new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger });
        var none = new FakeTaskWorker("Download missing subtitles", "DownloadSubtitles", 1);

        var list = SchedulePlanner.Untimed(new[] { timed, interval, startup, none }, NoHistory, NoManaged);

        Assert.Equal(new[] { "Download missing subtitles", "At startup" }, list.Select(t => t.TaskName));
        Assert.Equal("Only when Jellyfin starts", list.Single(t => t.TaskName == "At startup").Now);
        Assert.Equal("Never runs on its own", list.Single(t => t.TaskName == "Download missing subtitles").Now);
    }

    [Fact]
    public void A_task_Medic_runs_monthly_has_a_time()
    {
        var monthly = new FakeTaskWorker("Clean Activity Log", "CleanActivityLog", 1);
        var managed = new Dictionary<string, ManagedRun> { [monthly.Id] = new ManagedRun { TaskId = monthly.Id } };

        Assert.Empty(SchedulePlanner.Untimed(new[] { monthly }, NoHistory, managed));
    }

    [Fact]
    public void Known_tasks_are_recommended_on_and_unknown_ones_are_your_call()
    {
        var subtitles = new FakeTaskWorker("Download missing subtitles", "DownloadSubtitles", 1);
        var unknown = new FakeTaskWorker("Some plugin's task", "SomePluginTask", 1);
        var restart = new FakeTaskWorker("Scheduled restart", ScheduledRestartTask.TaskKey, 1);
        var updates = new FakeTaskWorker("Restart Jellyfin for waiting updates", RestartForUpdatesTask.TaskKey, 1);

        var list = SchedulePlanner.Untimed(new[] { subtitles, unknown, restart, updates }, NoHistory, NoManaged)
            .ToDictionary(t => t.TaskName);

        Assert.Equal(UntimedTaskAdvice.On, list["Download missing subtitles"].Recommendation);
        Assert.Equal("Every 3–4 days", list["Download missing subtitles"].HowOften);
        Assert.Equal(UntimedTaskAdvice.YourCall, list["Some plugin's task"].Recommendation);
        Assert.Equal(UntimedTaskAdvice.Off, list["Scheduled restart"].Recommendation);
        Assert.Equal(UntimedTaskAdvice.On, list["Restart Jellyfin for waiting updates"].Recommendation);
        Assert.Equal("Its maker gives no description.", list["Some plugin's task"].WhatItDoes);
    }
}

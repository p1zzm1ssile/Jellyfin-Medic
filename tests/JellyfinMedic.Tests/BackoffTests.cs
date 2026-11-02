using JellyfinMedic.Api;
using JellyfinMedic.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

public sealed class BackoffTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();
    private readonly FakeTaskManager _tasks = new();

    public BackoffTests()
    {
        new TaskLifecycleListener(_tasks, _paths, NullLogger<TaskLifecycleListener>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void A_failed_task_moves_three_hours_later_until_it_succeeds()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(5, 0), task.Triggers.Single().TimeOfDayTicks);

        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_schedule_changed_during_a_back_off_is_kept()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        task.Triggers = new[] { Daily(4, 0) }; // changed by hand in Jellyfin's dashboard
        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(4, 0), task.Triggers.Single().TimeOfDayTicks);

        // The back-off is over, so the next failure moves the new time.
        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(7, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_task_its_owner_chose_to_keep_stays_at_its_time()
    {
        var kept = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
        var planned = new FakeTaskWorker("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(2, 0));
        ScheduleStorage.SaveChoice(_paths, kept.Id, ScheduleStorage.ChoiceKeep);
        ScheduleStorage.SaveChoice(_paths, planned.Id, ScheduleStorage.ChoiceMedic);

        _tasks.Complete(kept, TaskCompletionStatus.Failed);
        _tasks.Complete(planned, TaskCompletionStatus.Failed);

        Assert.Equal(At(2, 0), kept.Triggers.Single().TimeOfDayTicks);
        Assert.Equal(At(5, 0), planned.Triggers.Single().TimeOfDayTicks);
    }

    [Theory]
    [InlineData(18, 23, 16, 0, false)] // 19:00
    [InlineData(18, 23, 15, 0, false)] // 18:00, as the hours to avoid start
    [InlineData(18, 23, 20, 0, true)] // 23:00, as they end
    [InlineData(22, 2, 19, 30, false)] // 22:30, in hours that run past midnight
    [InlineData(22, 2, 21, 30, false)] // 00:30
    [InlineData(22, 2, 23, 0, true)] // 02:00, as they end
    public void A_failed_task_is_not_moved_into_the_hours_to_avoid(int avoidFrom, int avoidTo, int hour, int minute, bool moves)
    {
        var settings = new PluginConfiguration { AvoidEnabled = true, AvoidStartMinute = avoidFrom * 60, AvoidEndMinute = avoidTo * 60 };

        var shifted = TaskLifecycleListener.Shifted(new[] { Daily(hour, minute) }, settings);

        Assert.Equal(moves, shifted is not null);
    }

    [Fact]
    public void One_time_in_the_hours_to_avoid_holds_the_whole_task_while_they_are_switched_on()
    {
        var triggers = new[] { Weekly(DayOfWeek.Monday, 2, 0), Weekly(DayOfWeek.Tuesday, 16, 0) };
        var settings = new PluginConfiguration { AvoidEnabled = true, AvoidStartMinute = 18 * 60, AvoidEndMinute = 23 * 60 };
        Assert.Null(TaskLifecycleListener.Shifted(triggers, settings));

        settings.AvoidEnabled = false;
        Assert.NotNull(TaskLifecycleListener.Shifted(triggers, settings));
    }

    [Fact]
    public void A_task_whose_move_cant_be_logged_stays_at_its_time()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
        string blocker = ScheduleStorage.AuditLogPath(_paths) + ".tmp";
        Directory.CreateDirectory(blocker); // the schedule changes log can't be written

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);

        // Nothing was left half done, so once the log can be written the back-off works as before.
        Directory.Delete(blocker);
        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(5, 0), task.Triggers.Single().TimeOfDayTicks);
        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_task_whose_original_time_cant_be_saved_stays_at_its_time()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
        Directory.CreateDirectory(ScheduleStorage.ProfilePath(_paths) + ".tmp"); // the back-off record can't be written

        _tasks.Complete(task, TaskCompletionStatus.Failed);

        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_move_Jellyfin_cant_save_isnt_left_in_the_log()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0)) { FailTriggerSaves = true };

        _tasks.Complete(task, TaskCompletionStatus.Failed);

        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);
        string? logged = ScheduleStorage.ReadList<MovementAudit>(ScheduleStorage.AuditLogPath(_paths)).LastOrDefault()?.NewTime;
        Assert.True(logged is null || logged == ScheduleStorage.FormatTriggers(task.Triggers), "Schedule changes ends with the task at " + logged);

        // Nothing was left half done, so once Jellyfin can save again the back-off works as before.
        task.FailTriggerSaves = false;
        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(5, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_back_off_ends_when_its_task_succeeds_even_if_the_log_cant_be_written()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
        string blocker = ScheduleStorage.AuditLogPath(_paths) + ".tmp";

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Directory.CreateDirectory(blocker);
        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(2, 0), task.Triggers.Single().TimeOfDayTicks);

        // The back-off is over, so the next failure moves the task again.
        Directory.Delete(blocker);
        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(5, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    [Fact]
    public void A_back_off_ends_after_a_schedule_change_even_if_the_log_cant_be_written()
    {
        var task = new FakeTaskWorker("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
        string blocker = ScheduleStorage.AuditLogPath(_paths) + ".tmp";

        _tasks.Complete(task, TaskCompletionStatus.Failed);
        task.Triggers = new[] { Daily(4, 0) }; // changed by hand in Jellyfin's dashboard
        Directory.CreateDirectory(blocker);
        _tasks.Complete(task, TaskCompletionStatus.Completed);
        Assert.Equal(At(4, 0), task.Triggers.Single().TimeOfDayTicks);

        // The back-off is over, so the next failure moves the new time.
        Directory.Delete(blocker);
        _tasks.Complete(task, TaskCompletionStatus.Failed);
        Assert.Equal(At(7, 0), task.Triggers.Single().TimeOfDayTicks);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private static long At(int hour, int minute) => new TimeSpan(hour, minute, 0).Ticks;
}

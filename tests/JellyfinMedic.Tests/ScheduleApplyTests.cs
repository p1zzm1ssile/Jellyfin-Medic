using System.Globalization;
using JellyfinMedic.Api;
using JellyfinMedic.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using static JellyfinMedic.Tests.FakeTaskWorker;

namespace JellyfinMedic.Tests;

/// <summary>Apply changes the schedule the owner previewed, or nothing.</summary>
public sealed class ScheduleApplyTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();
    private readonly FakeTaskManager _tasks = new();
    private readonly FakeTaskWorker _scan = new("Scan Media Library", "RefreshLibrary", 10, Daily(2, 0));
    private readonly FakeTaskWorker _clean = new("Clean Transcode Directory", "DeleteTranscodeFiles", 1, Daily(3, 0));
    private readonly ScheduleController _controller;

    public ScheduleApplyTests()
    {
        _tasks.ScheduledTasks = new[] { _scan, _clean };
        _controller = new ScheduleController(_tasks, _paths, NullLogger<ScheduleController>.Instance);
    }

    [Fact]
    public void The_same_plan_gives_the_same_hash_in_any_order_and_culture()
    {
        var hash = ScheduleController.PlanHash(new[] { DailyAt(_scan, 3, 0), DailyAt(_clean, 4, 0) });

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(hash, ScheduleController.PlanHash(new[] { DailyAt(_clean, 4, 0), DailyAt(_scan, 3, 0) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_changed_trigger_or_choice_gives_a_different_hash()
    {
        var hash = ScheduleController.PlanHash(new[] { DailyAt(_scan, 3, 0) });

        Assert.NotEqual(hash, ScheduleController.PlanHash(new[] { DailyAt(_scan, 3, 15) }));

        var kept = DailyAt(_scan, 3, 0);
        kept.Choice = ScheduleStorage.ChoiceKeep;
        Assert.NotEqual(hash, ScheduleController.PlanHash(new[] { kept }));

        var monthly = MonthlyOn(_scan, DayOfWeek.Monday);
        Assert.NotEqual(ScheduleController.PlanHash(new[] { monthly }), ScheduleController.PlanHash(new[] { MonthlyOn(_scan, DayOfWeek.Tuesday) }));
    }

    [Fact]
    public void Apply_with_a_stale_preview_changes_nothing()
    {
        string previewed = PreviewHash();
        ScheduleStorage.SaveChoice(_paths, _scan.Id, ScheduleStorage.ChoiceOff); // changed in another tab

        var result = _controller.ApplyRecommendedSchedule(previewed);

        var conflict = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("Preview again", Assert.IsType<string>(conflict.Value), StringComparison.Ordinal);
        Assert.Equal(Daily(2, 0).TimeOfDayTicks, _scan.Triggers.Single().TimeOfDayTicks);
        Assert.False(Directory.Exists(ScheduleStorage.BackupDir(_paths)));
    }

    [Fact]
    public void Apply_with_the_current_preview_applies_it()
    {
        ScheduleStorage.SaveChoice(_paths, _scan.Id, ScheduleStorage.ChoiceOff);

        var result = _controller.ApplyRecommendedSchedule(PreviewHash());

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(_scan.Triggers);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private string PreviewHash()
    {
        var preview = Assert.IsType<OkObjectResult>(_controller.PreviewScheduleDiff().Result).Value!;
        return Assert.IsType<string>(preview.GetType().GetProperty("PlanHash")?.GetValue(preview));
    }

    private static PlannedTask DailyAt(FakeTaskWorker worker, int hour, int minute) => new()
    {
        Worker = worker,
        Cadence = Cadence.Daily,
        CurrentSchedule = "Daily at 02:00",
        ProposedSchedule = $"Daily at {hour:00}:{minute:00}",
        Triggers = new[] { Daily(hour, minute) },
        StartMinutes = (hour * 60) + minute
    };

    private static PlannedTask MonthlyOn(FakeTaskWorker worker, DayOfWeek day) => new()
    {
        Worker = worker,
        Cadence = Cadence.Monthly,
        CurrentSchedule = "Daily at 02:00",
        ProposedSchedule = $"Monthly, first {day} at 03:00",
        Managed = new ManagedRun { TaskId = worker.Id, TaskName = worker.Name, Weekday = day, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks },
        StartMinutes = 3 * 60
    };
}

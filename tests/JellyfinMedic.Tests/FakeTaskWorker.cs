using Jellyfin.Data.Events;
using MediaBrowser.Model.Tasks;

namespace JellyfinMedic.Tests;

/// <summary>A scheduled task as the planner sees it: a name, its triggers and how long its last run took.</summary>
internal sealed class FakeTaskWorker : IScheduledTaskWorker
{
    private IReadOnlyList<TaskTriggerInfo> _triggers = Array.Empty<TaskTriggerInfo>();

    public FakeTaskWorker(string name, string key, double lastRunMinutes, params TaskTriggerInfo[] triggers)
    {
        Name = name;
        ScheduledTask = new FakeTask(name, key);
        Triggers = triggers;
        var end = new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);
        LastExecutionResult = new TaskResult
        {
            Name = name,
            Key = key,
            Status = TaskCompletionStatus.Completed,
            StartTimeUtc = end.AddMinutes(-lastRunMinutes),
            EndTimeUtc = end
        };
    }

    public event EventHandler<GenericEventArgs<double>>? TaskProgress
    {
        add { }
        remove { }
    }

    public IScheduledTask ScheduledTask { get; }

    public TaskResult LastExecutionResult { get; }

    public string Name { get; }

    public string Description => string.Empty;

    public string Category => "Tests";

    public TaskState State => TaskState.Idle;

    public double? CurrentProgress => null;

    public IReadOnlyList<TaskTriggerInfo> Triggers
    {
        get => _triggers;
        set => _triggers = FailTriggerSaves ? throw new IOException("The task's triggers couldn't be saved.") : value;
    }

    /// <summary>Makes setting the triggers throw, as Jellyfin does when it can't write them to disk.</summary>
    public bool FailTriggerSaves { get; set; }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public static TaskTriggerInfo Daily(int hour, int minute) => new()
    {
        Type = TaskTriggerInfoType.DailyTrigger,
        TimeOfDayTicks = new TimeSpan(hour, minute, 0).Ticks
    };

    public static TaskTriggerInfo Weekly(DayOfWeek day, int hour, int minute) => new()
    {
        Type = TaskTriggerInfoType.WeeklyTrigger,
        DayOfWeek = day,
        TimeOfDayTicks = new TimeSpan(hour, minute, 0).Ticks
    };

    public void ReloadTriggerEvents()
    {
    }

    public void Dispose()
    {
    }

    private sealed class FakeTask(string name, string key) : IScheduledTask
    {
        public string Name => name;

        public string Key => key;

        public string Description => string.Empty;

        public string Category => "Tests";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }
}

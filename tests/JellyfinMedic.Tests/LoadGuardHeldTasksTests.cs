using JellyfinMedic.Api;
using JellyfinMedic.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyfinMedic.Tests;

/// <summary>The held list the Dashboard reads is shared by every LoadGuard, so these tests run on their own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoadGuardHeldCollection
{
    public const string Name = "LoadGuard held tasks";
}

[Collection(LoadGuardHeldCollection.Name)]
public sealed class LoadGuardHeldTasksTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();
    private readonly FakeTaskManager _tasks = new();
    private readonly FakeTaskWorker _scan = new("Scan media library", "RefreshLibrary", 30);
    private readonly FakeTaskWorker _trickplay = new("Generate trickplay images", "RefreshTrickplayImages", 30);

    // The list as saved for this test's server, which a restart reads back.
    private List<PausedTask> Saved() =>
        ScheduleStorage.ReadList<PausedTask>(Path.Combine(ScheduleStorage.DataDir(_paths), "loadguard_deferred.json"));

    [Fact]
    public async Task A_held_task_that_starts_on_its_own_is_taken_off_the_list()
    {
        using var guard = new LoadGuard(_tasks, _paths, NullLogger<LoadGuard>.Instance);
        await guard.StartAsync(CancellationToken.None);
        guard.Defer(_scan);
        guard.Defer(_trickplay);

        _tasks.Complete(_scan, TaskCompletionStatus.Completed);
        guard.DropStarted();
        await guard.StopAsync(CancellationToken.None);

        Assert.Equal(_trickplay.Id, Assert.Single(Saved()).TaskId);
    }

    [Fact]
    public async Task A_task_stopped_after_it_started_stays_held()
    {
        using var guard = new LoadGuard(_tasks, _paths, NullLogger<LoadGuard>.Instance);
        await guard.StartAsync(CancellationToken.None);
        _tasks.Complete(_scan, TaskCompletionStatus.Cancelled);
        guard.Defer(_scan);

        guard.DropStarted();
        await guard.StopAsync(CancellationToken.None);

        Assert.Equal(_scan.Id, Assert.Single(Saved()).TaskId);
    }

    [Fact]
    public void Switching_LoadGuard_off_records_each_task_it_lets_go()
    {
        using var guard = new LoadGuard(_tasks, _paths, NullLogger<LoadGuard>.Instance);
        guard.Defer(_scan);
        guard.Defer(_trickplay);

        guard.ReleaseAll();

        Assert.Empty(Saved());
        var events = LoadGuardLog.Load(_paths);
        Assert.All(events, e => Assert.Equal("released", e.Action));
        Assert.Equal(new[] { "Generate trickplay images", "Scan media library" }, events.Select(e => e.TaskName).Order());
    }

    [Fact]
    public void A_restart_names_the_tasks_LoadGuard_is_holding()
    {
        var stopped = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        PausedTask Held(string name) => new() { TaskId = name, TaskName = name, StoppedUtc = stopped };

        Assert.Null(LoadGuard.HeldNames(Array.Empty<PausedTask>()));
        Assert.Equal("\"Scan media library\"", LoadGuard.HeldNames(new[] { Held("Scan media library") }));
        Assert.Equal(
            "\"Scan media library\", \"Extract chapter images\" and \"Generate trickplay images\"",
            LoadGuard.HeldNames(new[] { Held("Scan media library"), Held("Extract chapter images"), Held("Generate trickplay images") }));
    }

    [Fact]
    public void A_task_held_for_a_day_is_let_go_instead_of_started()
    {
        var log = new ListLogger<LoadGuard>();
        using var guard = new LoadGuard(_tasks, _paths, log);
        guard.Defer(_scan);
        var now = DateTime.UtcNow;

        guard.DropExpired(now + LoadGuard.KeepAcrossRestart - TimeSpan.FromMinutes(1));
        Assert.Single(Saved());

        guard.DropExpired(now + LoadGuard.KeepAcrossRestart + TimeSpan.FromMinutes(1));
        Assert.Empty(Saved());
        Assert.Empty(LoadGuard.Paused);
        Assert.Contains(log.Lines, l => l.Contains("'Scan media library'", StringComparison.Ordinal) && l.Contains("24 hours", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_saved_task_held_for_a_day_is_let_go_after_a_restart()
    {
        var now = DateTime.UtcNow;
        ScheduleStorage.WriteJson(Path.Combine(ScheduleStorage.DataDir(_paths), "loadguard_deferred.json"), new[]
        {
            new PausedTask { TaskId = _scan.Id.ToString(), TaskName = _scan.Name, StoppedUtc = now.AddHours(-25) },
            new PausedTask { TaskId = _trickplay.Id.ToString(), TaskName = _trickplay.Name, StoppedUtc = now.AddHours(-1) }
        });

        var log = new ListLogger<LoadGuard>();
        using var guard = new LoadGuard(_tasks, _paths, log);
        await guard.StartAsync(CancellationToken.None);
        await guard.StopAsync(CancellationToken.None);

        Assert.Equal(_trickplay.Id.ToString(), Assert.Single(Saved()).TaskId);
        Assert.Contains(log.Lines, l => l.Contains("'Scan media library'", StringComparison.Ordinal) && l.Contains("24 hours", StringComparison.Ordinal));
        guard.DropFirst();
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}

/// <summary>Both restart tasks can reach the restart at once, as neither waits for the other.</summary>
public sealed class SafeRestartLatchTests
{
    [Fact]
    public void Medic_asks_Jellyfin_to_restart_once_per_process()
    {
        var log = new ListLogger<SafeRestartLatchTests>();
        var refusing = new FakeSystemManager(refuses: true);
        var manager = new FakeSystemManager(refuses: false);

        // A request that fails doesn't count, so a later run can still ask.
        Assert.False(SafeRestart.Restart("scheduled restart", null!, new OneService(refusing), log));
        Assert.True(SafeRestart.Restart("scheduled restart", null!, new OneService(manager), log));
        Assert.False(SafeRestart.Restart("to finish installing updates", null!, new OneService(manager), log));

        Assert.Equal(1, manager.Restarts);
        Assert.Contains(log.Lines, l => l.Contains("already asked Jellyfin to restart", StringComparison.Ordinal));
    }

    // Found by its Restart method, as SafeRestart finds Jellyfin's ISystemManager.
    private sealed class FakeSystemManager(bool refuses)
    {
        public int Restarts { get; private set; }

        public void Restart()
        {
            if (refuses)
            {
                throw new InvalidOperationException("Jellyfin is still starting.");
            }

            Restarts++;
        }
    }

    private sealed class OneService(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => service;
    }
}

/// <summary>Keeps every message, so a test can check what was said.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add(formatter(state, exception));
        }
    }
}

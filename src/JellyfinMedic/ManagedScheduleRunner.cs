using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Runs the tasks Medic schedules monthly (on the first of a chosen weekday, for
/// example the first Thursday). Jellyfin has no monthly trigger, so those tasks have no
/// Jellyfin triggers of their own and this service starts them, the same way the Run
/// button on the Scheduled Tasks page does.
///
/// If the server is off at the scheduled time, the task runs at the same time the
/// following night instead.
/// </summary>
public sealed class ManagedScheduleRunner : IHostedService, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(1);

    private readonly ITaskManager _taskManager;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ManagedScheduleRunner> _logger;
    private Timer? _timer;
    private int _busy;

    public ManagedScheduleRunner(
        ITaskManager taskManager,
        IApplicationPaths appPaths,
        ILogger<ManagedScheduleRunner> logger)
    {
        _taskManager = taskManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Tick(), null, CheckEvery, CheckEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void Tick()
    {
        // Skip this tick if the previous one is still going.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.Now; // Server local time, the same clock Jellyfin's own triggers use.
            var toStart = new List<IScheduledTaskWorker>();

            lock (ScheduleStorage.SyncRoot)
            {
                string path = ScheduleStorage.ManagedPath(_appPaths);
                var runs = ScheduleStorage.ReadList<ManagedRun>(path);
                bool changed = false;

                foreach (var run in runs.Where(r => IsDue(r, now)))
                {
                    var worker = ScheduleStorage.FindWorker(_taskManager, run.TaskId);
                    if (worker is null || worker.State != TaskState.Idle)
                    {
                        continue; // Missing, or already running: try again next minute.
                    }

                    run.LastStartedLocal = now;
                    changed = true;
                    toStart.Add(worker);
                }

                if (changed)
                {
                    ScheduleStorage.WriteJson(path, runs);
                }
            }

            // Start the tasks outside the lock.
            foreach (var worker in toStart)
            {
                try
                {
                    _logger.LogInformation("Jellyfin Medic: starting monthly task '{TaskName}'.", worker.Name);
                    _taskManager.Execute(worker, new TaskOptions());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Jellyfin Medic: could not start monthly task '{TaskName}'.", worker.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellyfin Medic: error while checking monthly tasks.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    internal static bool IsDue(ManagedRun run, DateTime now)
    {
        var slot = TimeSpan.FromTicks(run.TimeOfDayTicks);
        var dueThisMonth = DueThisMonth(run, now);

        if (now < dueThisMonth)
        {
            return false;
        }

        // Any start this month counts, even one before this slot: a run on the day the task had before it
        // was moved to a later one, or the time it was handed to Medic (its first run is next month).
        if (run.LastStartedLocal.HasValue && run.LastStartedLocal.Value >= new DateTime(now.Year, now.Month, 1))
        {
            return false; // Already done this month.
        }

        // Only start inside the hour after the slot, so a missed run waits for the same time next night.
        var timeOfDay = now.TimeOfDay;
        return timeOfDay >= slot && timeOfDay < slot + CatchUpWindow;
    }

    internal static DateTime DueThisMonth(ManagedRun run, DateTime now) =>
        ScheduleStorage.FirstWeekdayOfMonth(now.Year, now.Month, run.Weekday).AddTicks(run.TimeOfDayTicks);
}

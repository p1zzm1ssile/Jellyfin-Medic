using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinMedic.Services;

/// <summary>
/// Shared by Medic's two restart tasks: waits for a safe moment (nobody watching, no track cleanup,
/// no Live TV recording, no other scheduled task busy, and still so two minutes later), then asks
/// Jellyfin to restart itself, exactly like the Dashboard's Restart button. If it isn't safe, it checks
/// every 5 minutes for up to an hour, then gives up.
/// </summary>
public static class SafeRestart
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);

    // Between two episodes nothing is playing for a moment, so a quiet check is confirmed this much later.
    private static readonly TimeSpan ConfirmAfter = TimeSpan.FromMinutes(2);

    public static async Task<bool> WhenSafeAsync(
        string why,
        string ownKey,
        IServerApplicationHost host,
        ISessionManager sessions,
        ITaskManager tasks,
        IServiceProvider services,
        ILogger logger,
        IProgress<double> progress,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GiveUpAfter;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? blocker = await BlockerAsync(sessions, tasks, services, ownKey, ct).ConfigureAwait(false);
            if (blocker is null)
            {
                await Task.Delay(ConfirmAfter, ct).ConfigureAwait(false);
                blocker = await BlockerAsync(sessions, tasks, services, ownKey, ct).ConfigureAwait(false);
                if (blocker is null)
                {
                    break;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                logger.LogInformation("Jellyfin Medic: didn't restart ({Why}) because {Reason}. It will try again at its next scheduled time.", why, blocker);
                return false;
            }

            logger.LogInformation("Jellyfin Medic: waiting to restart ({Why}) because {Reason}", why, blocker);
            progress.Report(Math.Min(95, 100 * (1 - (deadline - DateTime.UtcNow).TotalMinutes / GiveUpAfter.TotalMinutes)));
            await Task.Delay(CheckEvery, ct).ConfigureAwait(false);
        }

        progress.Report(100);
        logger.LogWarning("Jellyfin Medic: restarting Jellyfin ({Why}); nobody is watching and nothing else is running", why);
        if (services.GetService(typeof(MediaBrowser.Common.Configuration.IApplicationPaths)) is MediaBrowser.Common.Configuration.IApplicationPaths paths)
        {
            WeeklyLedger.Init(paths.PluginConfigurationsPath);
            WeeklyLedger.AddRestart();
        }

        if (!RestartNow(host, services, logger))
        {
            logger.LogWarning("Jellyfin Medic: couldn't ask Jellyfin to restart on this version. Restart it from the Dashboard instead.");
            return false;
        }

        return true;
    }

    private static async Task<string?> BlockerAsync(ISessionManager sessions, ITaskManager tasks, IServiceProvider services, string ownKey, CancellationToken ct)
    {
        try
        {
            int watching = sessions.Sessions.Cast<object>().Count(s => SettingsReader.Get(s, "NowPlayingItem") is not null);
            if (watching > 0)
            {
                return watching == 1 ? "someone is watching" : $"{watching} people are watching";
            }
        }
        catch
        {
            return "Medic couldn't check who's watching";
        }

        if (TrackCleaner.CurrentProgress().Running)
        {
            return "track cleanup is running";
        }

        // A restart in the middle of a Live TV recording would cut it short.
        try
        {
            if (services.GetService(typeof(ILiveTvManager)) is ILiveTvManager liveTv
                && (await liveTv.GetTimers(new TimerQuery { IsActive = true }, ct).ConfigureAwait(false)).TotalRecordCount > 0)
            {
                return "a Live TV recording is in progress";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If recordings can't be listed (no Live TV set up), don't block on it.
        }

        try
        {
            var busy = tasks.ScheduledTasks.FirstOrDefault(t =>
                t.State == TaskState.Running && !string.Equals(t.ScheduledTask.Key, ownKey, StringComparison.Ordinal));
            if (busy is not null)
            {
                return $"\"{busy.Name}\" is running";
            }
        }
        catch
        {
            // If tasks can't be listed, don't block on it.
        }

        return null;
    }

    // Jellyfin 10.9 and later restart through ISystemManager; earlier versions through the application host.
    // Found by name, so this keeps working if the interface moves between versions.
    private static bool RestartNow(IServerApplicationHost host, IServiceProvider services, ILogger logger)
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MediaBrowser.Controller.ISystemManager", false))
                .FirstOrDefault(t => t is not null);
            var manager = type is null ? null : services.GetService(type);
            var method = manager?.GetType().GetMethod("Restart", Type.EmptyTypes);
            if (method is not null)
            {
                method.Invoke(manager, null);
                return true;
            }

            var hostRestart = host.GetType().GetMethod("Restart", Type.EmptyTypes);
            if (hostRestart is not null)
            {
                hostRestart.Invoke(host, null);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jellyfin Medic: restart request failed");
        }

        return false;
    }
}

/// <summary>Restarts Jellyfin only when plugin updates are waiting for one. It has no time set until someone adds one.</summary>
public sealed class RestartForUpdatesTask : IScheduledTask
{
    public const string TaskKey = "JellyfinMedicRestartForUpdates";

    private readonly IServerApplicationHost _host;
    private readonly ISessionManager _sessions;
    private readonly ITaskManager _tasks;
    private readonly IServiceProvider _services;
    private readonly ILogger<RestartForUpdatesTask> _logger;

    public RestartForUpdatesTask(IServerApplicationHost host, ISessionManager sessions, ITaskManager tasks, IServiceProvider services, ILogger<RestartForUpdatesTask> logger)
    {
        _host = host;
        _sessions = sessions;
        _tasks = tasks;
        _services = services;
        _logger = logger;
    }

    public string Name => "Restart Jellyfin for waiting updates";

    public string Key => TaskKey;

    public string Description =>
        "If plugin updates are waiting for a restart, restarts Jellyfin, but only when nobody is watching, no track cleanup or Live TV recording is running "
        + "and no other scheduled task is busy, and it's still so two minutes later. If it isn't safe, it checks again every 5 minutes for up to an hour, then waits for its next run. "
        + "It has no time set to begin with: add one (04:00, say) to turn it on.";

    public string Category => "Jellyfin Medic";

    /// <summary>True when Jellyfin is waiting for a restart to finish installing something.</summary>
    public static bool RestartPending(IServerApplicationHost host) => SettingsReader.Get(host, "HasPendingRestart") is true;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!RestartPending(_host))
        {
            _logger.LogInformation("Jellyfin Medic: no updates are waiting for a restart");
            return;
        }

        await SafeRestart.WhenSafeAsync("to finish installing updates", TaskKey, _host, _sessions, _tasks, _services, _logger, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    // No time to begin with, like Scheduled restart: restarting the server is the admin's choice to make.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}

/// <summary>
/// A plain scheduled restart, for people who like to restart Jellyfin regularly (for example weekly,
/// to clear memory). It has no time set to begin with, so it never restarts anyone's server until they
/// choose a time under Scheduled Tasks.
/// </summary>
public sealed class ScheduledRestartTask : IScheduledTask
{
    public const string TaskKey = "JellyfinMedicScheduledRestart";

    private readonly IServerApplicationHost _host;
    private readonly ISessionManager _sessions;
    private readonly ITaskManager _tasks;
    private readonly IServiceProvider _services;
    private readonly ILogger<ScheduledRestartTask> _logger;

    public ScheduledRestartTask(IServerApplicationHost host, ISessionManager sessions, ITaskManager tasks, IServiceProvider services, ILogger<ScheduledRestartTask> logger)
    {
        _host = host;
        _sessions = sessions;
        _tasks = tasks;
        _services = services;
        _logger = logger;
    }

    public string Name => "Scheduled restart";

    public string Key => TaskKey;

    public string Description =>
        "Restarts Jellyfin at the times you set here, for example once a week to clear memory. It only restarts when nobody is watching, "
        + "no track cleanup or Live TV recording is running and no other scheduled task is busy (still so two minutes later); otherwise it checks again every 5 minutes for up to an hour. "
        + "It has no time set to begin with: add one to turn it on.";

    public string Category => "Jellyfin Medic";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        SafeRestart.WhenSafeAsync("scheduled restart", TaskKey, _host, _sessions, _tasks, _services, _logger, progress, cancellationToken);

    // No default time: a restart should never happen until someone chooses one.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}

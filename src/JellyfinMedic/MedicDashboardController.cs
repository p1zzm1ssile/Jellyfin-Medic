using JellyfinMedic.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinMedic.Api;

/// <summary>
/// The data behind the Dashboard's panels. Kept apart from MedicController, so splitting that one up
/// (issue #62) doesn't touch these routes. Not "DashboardController": Jellyfin has one, and the API
/// document would list these routes under its "Dashboard".
/// </summary>
[ApiController]
[Route("JellyfinMedic/Dashboard")]
[Authorize(Policy = Policies.RequiresElevation)] // Admins only: it lists accounts and the server's state.
public class MedicDashboardController : ControllerBase
{
    private readonly DashboardBuilder _builder;

    public MedicDashboardController(
        IServerConfigurationManager config,
        IApplicationPaths paths,
        ITaskManager tasks,
        ISessionManager sessions,
        IUserManager users,
        IInstallationManager installs,
        IServiceProvider services,
        ILogger<MedicDashboardController> logger)
    {
        WeeklyLedger.Init(paths.PluginConfigurationsPath);
        _builder = new DashboardBuilder(config, paths, tasks, sessions, users, installs, services, logger);
    }

    /// <summary>Busy hours, today, transcodes, disks, network, log errors, people and updates. Kept for a minute.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardData>> GetDashboard(CancellationToken cancellationToken) =>
        Ok(await _builder.DataAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>What's happening now, cheap enough to poll every 15 seconds.</summary>
    [HttpGet("Live")]
    public ActionResult<DashboardLive> GetLive() => Ok(_builder.Live());
}

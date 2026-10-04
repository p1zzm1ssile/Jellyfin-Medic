using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.ScheduledTasks;

/// <summary>
/// Builds picks for every user. Shows up under Scheduled Tasks, so Jellyfin Medic's
/// Task Advisor can move it into your quiet hours like any other task.
/// </summary>
public class BuildPicksTask : IScheduledTask
{
    private readonly PicksEngine _engine;
    private readonly IUserManager _userManager;
    private readonly ILogger<BuildPicksTask> _logger;

    public BuildPicksTask(PicksEngine engine, IUserManager userManager, ILogger<BuildPicksTask> logger)
    {
        _engine = engine;
        _userManager = userManager;
        _logger = logger;
    }

    public string Name => "Build personal picks";

    public string Key => "MedicPicksBuild";

    public string Description => "Builds each user's picks from their viewing habits and refreshes their private playlist.";

    public string Category => "Medic Picks";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
#if JELLYFIN12
        var userIds = _userManager.GetUsers().Select(u => u.Id).ToList();
#else
        var userIds = _userManager.Users.Select(u => u.Id).ToList();
#endif

        var context = _engine.CreateRunContext();
        _logger.LogInformation(
            "Medic Picks: building picks for {Count} users (Discover {State})",
            userIds.Count,
            string.IsNullOrEmpty(context.TmdbKey) ? "off, no TMDb key" : "on");

        for (var i = 0; i < userIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _engine.BuildForUserAsync(userIds[i], context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medic Picks: failed for user {UserId}", userIds[i]);
            }

            progress.Report(100.0 * (i + 1) / Math.Max(1, userIds.Count));
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
            }
        };
    }
}

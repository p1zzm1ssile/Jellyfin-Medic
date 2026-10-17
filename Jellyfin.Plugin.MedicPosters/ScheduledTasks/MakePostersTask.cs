using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicPosters.Posters;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MedicPosters.ScheduledTasks;

/// <summary>
/// Makes posters for films that don't have one yet. Shows up under Scheduled Tasks, so Jellyfin Medic
/// can move it into your quiet hours like any other task.
/// </summary>
public class MakePostersTask : IScheduledTask
{
    private readonly PosterService _service;

    public MakePostersTask(PosterService service)
    {
        _service = service;
    }

    public string Name => "Make animated posters";

    public string Key => "MedicPostersMake";

    public string Description => "Makes an animated poster for each film that doesn't have one from Medic Posters yet.";

    public string Category => "Medic Posters";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _service.RunAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}

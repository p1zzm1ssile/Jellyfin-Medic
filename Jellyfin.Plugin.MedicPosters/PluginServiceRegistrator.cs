using Jellyfin.Plugin.MedicPosters.Posters;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MedicPosters;

/// <summary>Registers Medic Posters' services. The scheduled task and API controller are discovered by Jellyfin automatically.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PosterStore>();
        serviceCollection.AddSingleton<PosterMaker>();
        serviceCollection.AddSingleton<PosterService>();

        // Puts the animated poster back when a metadata refresh swaps the film's own one in again.
        serviceCollection.AddHostedService<PosterKeeper>();
    }
}

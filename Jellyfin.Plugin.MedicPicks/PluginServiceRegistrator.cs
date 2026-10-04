using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MedicPicks;

/// <summary>
/// Registers Medic Picks services with Jellyfin's dependency injection.
/// The scheduled task and API controller are discovered by Jellyfin automatically.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PicksStore>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<PicksEngine>();
    }
}

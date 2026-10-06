using Jellyfin.Plugin.MedicPicks.Picks;
using Jellyfin.Plugin.MedicPicks.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
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
        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<RequestTracker>();
        serviceCollection.AddSingleton<PicksEngine>();

        // Adds the "My picks" link to everyone's web menu (see Web/MenuLink.cs).
        serviceCollection.AddTransient<IStartupFilter, MenuLinkStartupFilter>();
    }
}

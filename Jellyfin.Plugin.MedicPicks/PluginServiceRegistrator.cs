using Jellyfin.Plugin.MedicPicks.Picks;
using Jellyfin.Plugin.MedicPicks.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Medic.Shared;
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
        // Calls that carry an API key, which don't follow redirects (see ServiceHttp.KeyClient).
        ServiceHttp.AddKeyClient(serviceCollection, applicationHost);
        serviceCollection.AddSingleton<PicksStore>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<RequestTracker>();
        serviceCollection.AddSingleton<PicksEngine>();

        // Removes the saved Seerr key when the Seerr address moves to another server (see Picks/SeerrKeyGuard.cs).
        serviceCollection.AddHostedService<SeerrKeyGuard>();

        // Adds the "My picks" link to everyone's web menu (see Web/MenuLink.cs).
        serviceCollection.AddTransient<IStartupFilter, MenuLinkStartupFilter>();
    }
}

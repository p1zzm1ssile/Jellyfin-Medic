using Jellyfin.Plugin.MedicProfiles.Arr;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Medic.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MedicProfiles;

/// <summary>Registers Medic Profiles' services. The API controller is discovered by Jellyfin automatically.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Calls that carry an API key, which don't follow redirects (see ServiceHttp.KeyClient).
        ServiceHttp.AddKeyClient(serviceCollection, applicationHost);
        serviceCollection.AddSingleton<ArrStore>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddSingleton<DownloadsService>();
        serviceCollection.AddSingleton<ProfileAdvisor>();
        serviceCollection.AddSingleton<IndexersService>();
        serviceCollection.AddSingleton<QbitFileFilter>();
    }
}

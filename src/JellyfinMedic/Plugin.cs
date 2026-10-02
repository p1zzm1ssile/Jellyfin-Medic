using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JellyfinMedic.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMedic;

/// <summary>Medic's own settings, saved by Jellyfin with the plugin.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // Never schedule tasks between these hours (wraps past midnight if needed).
    public bool AvoidEnabled { get; set; }

    public int AvoidStartHour { get; set; } = 18;

    public int AvoidEndHour { get; set; } = 23;

    // Accounts with no activity for this many days are reported as inactive.
    public int InactiveUserDays { get; set; } = 90;
}

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataMigration.Run(applicationPaths);
    }

    public override string Name => "Jellyfin Medic";

    public override Guid Id => Guid.Parse("8d1f5c2e-6b4a-4f7e-9c3d-2a7b5e1f0c94");

    public override string Description => "Health checks, tests and usage-aware task scheduling for your Jellyfin server.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "JellyfinMedic",
                DisplayName = "Jellyfin Medic",
                EmbeddedResourcePath = "JellyfinMedic.medic.html",

                // Adds "Jellyfin Medic" to the Plugins section of the dashboard sidebar.
                EnableInMainMenu = true,
                MenuIcon = "healing"
            }
        };
    }
}

/// <summary>Starts Medic's background services with the server.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<TaskLifecycleListener>();
        serviceCollection.AddHostedService<ManagedScheduleRunner>();
        serviceCollection.AddHostedService<UsageSampler>();
    }
}

/// <summary>
/// Brings data over from Task Advisor and Setup Optimiser the first time Medic starts:
/// schedule backups, run history, monthly tasks, the viewing pattern, speed test and ignored
/// findings. Files are copied, never moved, and nothing already in Medic's folder is replaced.
/// </summary>
public static class DataMigration
{
    public static void Run(IApplicationPaths paths)
    {
        try
        {
            string target = Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic");
            Directory.CreateDirectory(target);

            foreach (var source in new[] { "TaskAdvisor", "SetupOptimiser" })
            {
                string from = Path.Combine(paths.PluginConfigurationsPath, source);
                if (!Directory.Exists(from))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(from, file);
                    string destination = Path.Combine(target, relative);
                    if (File.Exists(destination))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }
        }
        catch
        {
            // Migration is best-effort: Medic works from an empty folder too.
        }
    }
}

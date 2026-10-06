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

    // Load guard: stop extra heavy tasks if memory climbs past the ceiling, then restart them
    // one at a time once it recovers. On by default at a safe ceiling.
    public bool LoadGuardEnabled { get; set; } = true;

    public int MemoryCeilingPercent { get; set; } = 85;

    // Scheduling help: "off" (you run Preview/Apply) or "suggest" (Medic flags a better schedule
    // in the report but waits for you to Apply). Default: suggest.
    public string ScheduleMode { get; set; } = "suggest";

    // Track cleaner (remux out unwanted audio/subtitle tracks from local files).
    public string TracksKeepLanguages { get; set; } = "eng";

    public bool TracksRemoveUndetermined { get; set; }          // off = keep undetermined tracks

    // Remove untagged subtitles while always keeping untagged audio.
    public bool TracksRemoveUntaggedSubtitles { get; set; }

    // With the above: keep the first untagged subtitle in a file that has no subtitle in your languages.
    public bool TracksKeepFirstUntaggedSubtitle { get; set; } = true;

    // Let a file's only subtitle go when it's untagged (never for films whose audio is tagged as a language you don't keep).
    public bool TracksAllowRemovingOnlySubtitle { get; set; }

    // Only remove tracks within a daily time window (local time). Start and end are hours, 0–23; overnight windows work.
    public bool TracksWindowEnabled { get; set; }

    public int TracksWindowStartHour { get; set; } = 1;

    public int TracksWindowEndHour { get; set; } = 7;

    // Hold off starting the next file while anyone is watching.
    public bool TracksPauseWhileWatching { get; set; } = true;

    public bool TracksReplaceInPlace { get; set; }              // off = keep the original in a hidden .medic-originals folder beside it

    public int TracksConcurrentFiles { get; set; } = 1;

    public int TracksFfmpegThreads { get; set; } = 1;
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
        serviceCollection.AddHostedService<LoadGuard>();

        // Shows the issue you're fixing on the Jellyfin page a Medic link sends you to (see PageHelper.cs).
        serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, PageHelperStartupFilter>();
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

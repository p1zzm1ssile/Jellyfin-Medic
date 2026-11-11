using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>A library and the facts the checks need about it.</summary>
public sealed class LibraryFacts
{
    // Library ID without dashes, the form plugins store in their settings.
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }

    public List<string> Locations { get; set; } = new();

    public object? Options { get; set; }

    public long? ItemCount { get; set; }

    // Share of sampled items that are .strm files (IPTV / streamed content), 0 to 1.
    public double? StreamedShare { get; set; }

    public bool IsStreamed => StreamedShare >= 0.5;
}

/// <summary>
/// Runs every check and returns specs plus findings. Read-only: it never changes a setting.
/// </summary>
public sealed partial class DiagnosticsEngine
{
    private const string AreaHardware = "Hardware & transcoding";
    private const string AreaStorage = "Storage";
    private const string AreaServer = "Server settings";
    private const string AreaLibraries = "Libraries";
    private const string AreaLiveTv = "Live TV";
    private const string AreaNetwork = "Network";
    private const string AreaTasks = "Scheduled tasks";
    private const string AreaUsage = "Usage & peak times";

    private const string WhereTranscoding = "Dashboard → Playback → Transcoding";
    private const string WhereTrickplay = "Dashboard → Playback → Trickplay";
    private const string WhereCustomCss = "Dashboard → General → Custom CSS";
    private const string AreaTheme = "Themes";
    // Where to change how Jellyfin is run (devices, storage, memory), worded for this platform.
    private static string WhereDocker => HostPlatform.WhereRunSettings;

    private const string WhereGeneral = "Dashboard → General";
    private const string WhereLibraries = "Dashboard → Libraries → (library) → Manage library";
    private const string WhereTasks = "Medic → Schedule, or Dashboard → Scheduled Tasks";

    private readonly IServerConfigurationManager _config;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly ITaskManager _tasks;
    private readonly IPluginManager _plugins;
    private readonly IServerApplicationHost _host;
    private readonly ILogger _logger;

    private DiagnosticReport _report = new();

    public DiagnosticsEngine(
        IServerConfigurationManager config,
        IApplicationPaths paths,
        ILibraryManager library,
        ITaskManager tasks,
        IPluginManager plugins,
        IServerApplicationHost host,
        ILogger logger)
    {
        _config = config;
        _paths = paths;
        _library = library;
        _tasks = tasks;
        _plugins = plugins;
        _host = host;
        _logger = logger;
    }

    private List<LibraryFacts>? _libraries;

    /// <summary>Libraries with item counts, loaded once per request.</summary>
    public List<LibraryFacts> Libraries => _libraries ??= LoadLibraries();

    /// <summary>What the plugin-specific rules need to know about this server.</summary>
    public PluginContext CreatePluginContext()
    {
        int tuners = (SettingsReader.List(TryConfig("livetv"), "TunerHosts") ?? new List<string>()).Count;
        bool iptvPlugin = _plugins.Plugins.Any(p => (SettingsReader.Text(p, "Name") ?? string.Empty).Contains("Xtream", StringComparison.OrdinalIgnoreCase));
        return new PluginContext
        {
            Libraries = Libraries,
            CpuThreads = Environment.ProcessorCount,
            LiveTvConfigured = tuners > 0 || iptvPlugin
        };
    }

    /// <summary>Plugin repositories and add-on scripts whose addresses should be checked.</summary>
    public List<LinkTarget> LinksToCheck()
    {
        var links = new List<LinkTarget>();

        if (SettingsReader.Get(_config.Configuration, "PluginRepositories") is System.Collections.IEnumerable repos)
        {
            foreach (var repo in repos.Cast<object>())
            {
                string? url = SettingsReader.Text(repo, "Url");
                if (!string.IsNullOrWhiteSpace(url) && SettingsReader.Bool(repo, "Enabled") != false)
                {
                    links.Add(new LinkTarget { Kind = "repository", Name = SettingsReader.Text(repo, "Name") ?? url, Url = url.Trim() });
                }
            }
        }

        try
        {
            string injector = Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Plugin.JavaScriptInjector.xml");
            bool injectorActive = _plugins.Plugins.Any(p =>
                (SettingsReader.Text(p, "Name") ?? string.Empty).Contains("JavaScript Injector", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(SettingsReader.Text(p, "Manifest.Status"), "Active", StringComparison.OrdinalIgnoreCase));
            if (injectorActive && File.Exists(injector))
            {
                var doc = System.Xml.Linq.XDocument.Load(injector);
                foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "CustomJavaScriptEntry"))
                {
                    string name = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? "script";
                    string code = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Script")?.Value ?? string.Empty;
                    bool enabled = !string.Equals(entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
                    if (!enabled)
                    {
                        continue;
                    }

                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"https?://[^\s'""`<>()]+\.(?:js|css)(?=$|[\s'""`<>()?;])(?:\?[^\s'""`<>()]*)?"))
                    {
                        links.Add(new LinkTarget { Kind = "script", Name = name, Url = m.Value });
                    }
                }
            }
        }
        catch
        {
            // Unreadable JavaScript Injector settings: skip the script checks.
        }

        // Themes loaded by the custom CSS (Dashboard → General → Custom CSS).
        foreach (var (url, _) in ThemeImports(CustomCss()))
        {
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(new LinkTarget { Kind = "theme", Name = url, Url = url });
            }
        }

        return links;
    }

    public DiagnosticReport Run(UsageSummary usage, List<PluginReport> pluginReports, List<LinkTarget>? links = null, Dictionary<string, string>? linkResults = null, List<Finding>? extra = null)
    {
        _report = new DiagnosticReport();

        // Guarded like the checks: if reading the hardware or listing the specs fails, the report
        // still comes back with everything else, instead of the whole request failing.
        var hw = new HardwareInfo();
        Guard(AreaHardware, () => hw = SystemProbe.Probe());
        var server = (object)_config.Configuration;
        var encoding = TryConfig("encoding");
        var network = TryConfig("network");
        var liveTv = TryConfig("livetv");
        var libraries = Libraries;

        Guard(AreaHardware, () => AddSpecs(hw, encoding, libraries, pluginReports.Count));

        Guard(AreaHardware, () => CheckTranscoding(hw, encoding));
        Guard(AreaStorage, () => CheckStorage(libraries, encoding));
        Guard(AreaServer, () => CheckServer(server, hw, libraries));
        Guard(AreaServer, () => CheckPerformance(server, hw, pluginReports.Count));
        Guard(AreaTheme, CheckTheme);
        Guard("Logs", CheckCriticalErrors);
        Guard(AreaServer, CheckResourceSpikes);
        Guard(AreaLibraries, () => CheckLibraries(libraries));
        Guard(AreaLiveTv, () => CheckLiveTv(liveTv));
        Guard(AreaNetwork, () => CheckNetwork(network));
        Guard(AreaTasks, () => CheckTasks(usage));
        Guard(AreaTasks, () => SuggestSchedule(usage));
        Guard(AreaUsage, () => CheckUsage(usage, encoding, hw));
        Guard("Plugins", () => CheckLinks(links, linkResults));

        Guard(AreaTasks, () =>
        {
            var events = LoadGuardLog.Load(_paths);
            var recent = events.Where(e => e.TimestampUtc > DateTime.UtcNow.AddDays(-7) && e.Action == "stopped").ToList();
            if (recent.Count > 0)
            {
                var last = recent[0];
                Add(AreaTasks, Sev.Improve, "Tasks have been overloading memory",
                    $"Medic stopped {recent.Count} heavy task run(s) in the last 7 days to keep the server up (last: {last.TaskName}, memory {last.MemoryUsedGb:0.#} GB)",
                    HostPlatform.IsContainer ? "Space these tasks out, or give the container more memory" : "Space these tasks out, or add memory",
                    "When several heavy scans run at once they can use all the memory and crash the server. Medic's load guard stopped the extras and restarted them later, but it's better to stop them colliding: Apply the recommended schedule so they don't overlap, and check whether another plugin or a manual scan is starting them at the same time.",
                    "Medic → Schedule");
            }
        });

        _report.Findings.AddRange(pluginReports.SelectMany(p => p.Findings));
        if (extra is not null)
        {
            _report.Findings.AddRange(extra);
        }

        Guard("Plugins", CheckOldPlugins);
        Guard("Storage", () => _report.Findings.AddRange(DiskHealth.Check()));

        // One "all good" line for any area with nothing to report.
        foreach (var area in new[] { AreaHardware, AreaStorage, AreaServer, AreaLibraries, AreaLiveTv, AreaNetwork, AreaTasks, "Plugins", AreaTheme, "Users and access", "Security", "Logs" })
        {
            if (!_report.Findings.Any(f => f.Area == area))
            {
                Add(area, Sev.Good, "No problems found", string.Empty, string.Empty, string.Empty, string.Empty);
            }
        }

        IgnoreStore.Apply(_report.Findings, IgnoreStore.Load(_paths));

        _report.Findings = _report.Findings
            .OrderBy(f => f.Ignored ? 1 : 0)
            .ThenBy(f => Sev.Rank(f.Severity))
            .ThenBy(f => f.Area, StringComparer.Ordinal)
            .ToList();

        return _report;
    }

    /// <summary>Task Advisor and Setup Optimiser are replaced by Medic and shouldn't run alongside it.</summary>
    private void CheckOldPlugins()
    {
        var old = _plugins.Plugins
            .Select(p => SettingsReader.Text(p, "Name") ?? string.Empty)
            .Where(n => n.Equals("Task Advisor", StringComparison.OrdinalIgnoreCase) || n.Equals("Setup Optimiser", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (old.Count > 0)
        {
            Add("Plugins", Sev.Problem, old.Count == 1 ? $"{old[0]} is still installed" : "Task Advisor and Setup Optimiser are still installed",
                string.Join(", ", old), "Uninstall them, then restart Jellyfin",
                "Medic replaces both. While they're installed, two copies watch your tasks, so the same task can be moved or started twice.",
                "Dashboard → Plugins → My Plugins");
        }
    }

    /// <summary>Every core setting, for the "All settings" tab.</summary>
    public List<SettingRow> AllSettings()
    {
        var rows = new List<SettingRow>();
        rows.AddRange(SettingsReader.Flatten(_config.Configuration, "General"));
        rows.AddRange(SettingsReader.Flatten(TryConfig("encoding"), "Transcoding"));
        rows.AddRange(SettingsReader.Flatten(TryConfig("network"), "Networking"));
        rows.AddRange(SettingsReader.Flatten(TryConfig("livetv"), "Live TV"));
        rows.AddRange(SettingsReader.Flatten(TryConfig("metadata"), "Metadata"));
        foreach (var lib in LoadLibraries(countItems: false))
        {
            rows.AddRange(SettingsReader.Flatten(lib.Options, $"Library: {lib.Name}"));
        }

        return rows;
    }

    // ---------- Helpers ----------

    private List<LibraryFacts> LoadLibraries(bool countItems = true)
    {
        var result = new List<LibraryFacts>();
        IEnumerable<object> folders;
        try
        {
            folders = _library.GetVirtualFolders().Cast<object>().ToList();
        }
        catch
        {
            return result;
        }

        foreach (var folder in folders)
        {
            var facts = new LibraryFacts
            {
                Name = SettingsReader.Text(folder, "Name") ?? "Library",
                CollectionType = SettingsReader.Text(folder, "CollectionType"),
                Locations = SettingsReader.List(folder, "Locations") ?? new List<string>(),
                Options = SettingsReader.Get(folder, "LibraryOptions"),
                Id = Guid.TryParse(SettingsReader.Text(folder, "ItemId"), out var libraryId) ? libraryId.ToString("N") : string.Empty
            };

            if (countItems && Guid.TryParse(SettingsReader.Text(folder, "ItemId"), out var id))
            {
                try
                {
                    // A library's ID is its collection folder. Asking by ParentId lets Jellyfin map it to
                    // the real folders underneath; AncestorIds is the fallback if that returns nothing.
                    int count = _library.GetCount(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false });
                    bool useAncestors = false;
                    if (count == 0)
                    {
                        count = _library.GetCount(new InternalItemsQuery { AncestorIds = new[] { id }, Recursive = true, IsFolder = false });
                        useAncestors = count > 0;
                    }

                    facts.ItemCount = count;

                    var sample = useAncestors
                        ? _library.GetItemList(new InternalItemsQuery { AncestorIds = new[] { id }, Recursive = true, IsFolder = false, Limit = 40 })
                        : _library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false, Limit = 40 });

                    var paths = sample.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    if (paths.Count > 0)
                    {
                        facts.StreamedShare = paths.Count(p => p!.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) / (double)paths.Count;
                    }
                }
                catch
                {
                    // Counting isn't essential; leave as unknown.
                }
            }

            result.Add(facts);
        }

        return result;
    }

    private object? TryConfig(string key)
    {
        try
        {
            return _config.GetConfiguration(key);
        }
        catch
        {
            return null;
        }
    }

    private string TranscodePath(object? enc) => SettingsReader.TranscodePath(enc, _paths.CachePath);

    private string? LogLevel()
    {
        foreach (var file in new[] { "logging.json", "logging.default.json" })
        {
            string path = Path.Combine(_paths.ConfigurationDirectoryPath, file);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (doc.RootElement.TryGetProperty("Serilog", out var serilog) && serilog.TryGetProperty("MinimumLevel", out var min))
                {
                    if (min.ValueKind == JsonValueKind.String)
                    {
                        return min.GetString();
                    }

                    if (min.ValueKind == JsonValueKind.Object && min.TryGetProperty("Default", out var def))
                    {
                        return def.GetString();
                    }
                }
            }
            catch
            {
                // Unreadable logging file: skip the check.
            }
        }

        return null;
    }

    /// <summary>The installed plugin a scheduled task comes from, or null for Jellyfin's own tasks.</summary>
    private string? PluginNameFor(IScheduledTaskWorker task)
    {
        try
        {
            string? assembly = task.ScheduledTask?.GetType().Assembly.GetName().Name;
            if (assembly is null)
            {
                return null;
            }

            foreach (var plugin in _plugins.Plugins.Cast<object>())
            {
                if (SettingsReader.Get(plugin, "Instance") is { } instance &&
                    string.Equals(instance.GetType().Assembly.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase))
                {
                    return SettingsReader.Text(plugin, "Name");
                }
            }
        }
        catch
        {
            // Unknown owner.
        }

        return null;
    }

    internal static string Shorten(string text)
    {
        // Masked before the cut, which can leave too little of a token for it to be recognised.
        string masked = SettingsReader.MaskSecrets(text);
        return masked.Length <= 600 ? masked : masked[..600] + "…";
    }

    /// <summary>Heavy maintenance scheduled during the day, when people are likely to be watching.</summary>
    private void CheckDaytimeTasks(List<IScheduledTaskWorker> workers)
    {
        // Jellyfin's own heavy tasks by key (their names are translated); tasks from plugins by name.
        string[] heavyKeys =
        {
            "RefreshLibrary", "OptimizeDatabaseTask", "RefreshChapterImages", "RefreshTrickplayImages", "MoveTrickplayImages",
            "TaskExtractMediaSegments", "KeyframeExtraction", "RefreshPeople", "AudioNormalization", "DownloadSubtitles", "RefreshGuide"
        };
        string[] heavy =
        {
            "Scan Media Library", "Optimize", "Optimise", "Chapter", "Trickplay", "Media Segment", "Keyframe",
            "Detect and Analyze", "Refresh People", "Audio Normali", "Subtitle", "Refresh Guide"
        };

        var daytime = new List<string>();
        foreach (var worker in workers.Where(ScheduleStorage.IsVisible))
        {
            if (!heavyKeys.Contains(worker.ScheduledTask.Key) && !heavy.Any(h => worker.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var trigger in worker.Triggers ?? Array.Empty<TaskTriggerInfo>())
            {
                if (trigger.TimeOfDayTicks is not { } ticks)
                {
                    continue;
                }

                var time = TimeSpan.FromTicks(ticks);
                if (time.Hours >= 8 && time.Hours <= 22)
                {
                    daytime.Add($"{worker.Name} ({time.Hours:00}:{time.Minutes:00})");
                    break;
                }
            }
        }

        if (daytime.Count > 0)
        {
            Add(AreaTasks, Sev.Tip, daytime.Count == 1 ? "A heavy task runs during the day" : $"{daytime.Count} heavy tasks run during the day",
                string.Join(", ", daytime), "Fine if nobody watches then. Medic will check against your real viewing pattern",
                "These tasks work through the whole library or database. Medic is still learning when people watch; after a few days it checks these times against your real viewing instead.",
                "Medic → Schedule", id: "tasks.heavy-daytime");
        }
    }

    /// <summary>Plugin repositories and add-on scripts that don't respond.</summary>
    private void CheckLinks(List<LinkTarget>? links, Dictionary<string, string>? results)
    {
        if (links is null || results is null)
        {
            return;
        }

        foreach (var link in links)
        {
            if (!results.TryGetValue(link.Url, out var result) || result == "OK")
            {
                continue;
            }

            if (link.Kind == "repository")
            {
                bool paradox = link.Url.Contains("iamparadox.dev/jellyfin/manifest.json", StringComparison.OrdinalIgnoreCase);
                Add("Plugins", Sev.Tip, $"Plugin repository \"{link.Name}\" didn't respond when checked", $"{link.Url} ({result})",
                    paradox ? "The address is missing /plugins/ — use https://www.iamparadox.dev/jellyfin/plugins/manifest.json" : "If this keeps happening, check the address on the plugin's install page, or whether your server can reach the site",
                    "This may just be a temporary outage, so it's only worth acting on if it keeps happening. While it's down, plugins from this repository won't get updates. Fixing or removing a repository doesn't uninstall anything.",
                    "Dashboard → Plugins → Repositories");
            }
            else if (link.Kind == "theme")
            {
                Add(AreaTheme, Sev.Improve, "A theme in your custom CSS can't be loaded", $"{link.Url} ({result})",
                    "Check the theme's install page for its current address, or remove the line",
                    "The browser can't fetch it, so the theme doesn't apply and pages can look half-styled while it keeps trying.",
                    WhereCustomCss);
            }
            else
            {
                Add("Plugins", Sev.Improve, $"JavaScript Injector script \"{link.Name}\" couldn't load a file it needs", $"{link.Url} ({result})",
                    "Update the address from the add-on's install instructions, or delete the script",
                    "The browser tries to load it on every page and fails, so whatever it adds doesn't work.",
                    "Dashboard → Plugins → JavaScript Injector");
            }
        }
    }


    // A drive that can't be named counts as another drive: one warning too many beats none.
    internal static bool SameDrive(string? driveA, string? driveB) => driveA is not null && driveA == driveB;

    private static string? Nice(string? hwType) => hwType?.ToLowerInvariant() switch
    {
        null => null,
        "" or "none" => "None (CPU only)",
        "qsv" => "Intel QuickSync (QSV)",
        "vaapi" => "VAAPI",
        "nvenc" => "NVIDIA NVENC",
        "amf" => "AMD AMF",
        "v4l2m2m" => "Video4Linux2",
        "rkmpp" => "Rockchip MPP",
        "videotoolbox" => "Apple VideoToolbox",
        _ => hwType
    };

    private void SpaceSpec(string group, string label, string path)
    {
        var space = SystemProbe.Space(path);
        var mount = SystemProbe.Mount(path);
        string where = mount?.FsType switch
        {
            "tmpfs" => "RAM",
            { } fs when fs.StartsWith("fuse.shfs", StringComparison.Ordinal) => "Unraid user share",
            { } fs => fs,
            null => "unknown"
        };
        Spec(group, label, space is { } s ? $"{SystemProbe.Gb(s.FreeGb)} free of {SystemProbe.Gb(s.TotalGb)} ({where})" : $"unknown ({where})");
    }

    private void Spec(string group, string label, string value) =>
        _report.Specs.Add(new SpecItem { Group = group, Label = label, Value = value });

    // id: a stable name for findings whose title changes between checks (counts, times, percentages); see Finding.Id.
    private void Add(string area, string severity, string title, string current, string recommended, string why, string where, string id = "") =>
        _report.Findings.Add(NewFinding(area, severity, title, current, recommended, why, where, id));

    private static Finding NewFinding(string area, string severity, string title, string current, string recommended, string why, string where, string id = "") =>
        new()
        {
            Area = area,
            Severity = severity,
            Title = title,
            Id = id,
            Current = current,
            Recommended = recommended,
            Why = why,
            Where = where
        };

    private void Guard(string area, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin Medic: some {Area} checks couldn't run", area);
            Add(area, Sev.Tip, "Some checks in this area couldn't run", ex.GetType().Name, string.Empty,
                $"Medic hit an error reading these settings ({ex.Message}). The rest of the report is unaffected.", string.Empty);
        }
    }
}

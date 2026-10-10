using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
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
public sealed class DiagnosticsEngine
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

    private DiagnosticReport _report = new();

    public DiagnosticsEngine(
        IServerConfigurationManager config,
        IApplicationPaths paths,
        ILibraryManager library,
        ITaskManager tasks,
        IPluginManager plugins,
        IServerApplicationHost host)
    {
        _config = config;
        _paths = paths;
        _library = library;
        _tasks = tasks;
        _plugins = plugins;
        _host = host;
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

        var hw = SystemProbe.Probe();
        var server = (object)_config.Configuration;
        var encoding = TryConfig("encoding");
        var network = TryConfig("network");
        var liveTv = TryConfig("livetv");
        var libraries = Libraries;

        AddSpecs(hw, encoding, libraries, pluginReports.Count);

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

    // ---------- Specs ----------

    private void AddSpecs(HardwareInfo hw, object? encoding, List<LibraryFacts> libraries, int pluginCount)
    {
        string version = SettingsReader.Text(_host, "ApplicationVersionString")
                         ?? SettingsReader.Text(_host, "ApplicationVersion") ?? "unknown";
        Spec("Server", "Jellyfin version", version);
        Spec("Server", "Running on", HostPlatform.Label);
        Spec("Server", "Server name", string.IsNullOrWhiteSpace(_config.Configuration.ServerName) ? "(not set)" : _config.Configuration.ServerName);

        Spec("Hardware", "CPU", hw.CpuModel ?? "unknown");
        Spec("Hardware", "CPU threads available", hw.CpuThreads.ToString(CultureInfo.InvariantCulture));
        Spec("Hardware", "Memory Jellyfin can use", SystemProbe.Gb(hw.MemoryGb));
        if (hw.HostMemoryGb is { } host)
        {
            Spec("Hardware", "Memory in the machine", SystemProbe.Gb(host));
        }

        if (HostPlatform.IsLinux)
        {
            Spec("Hardware", "GPU", hw.GpuDescription);
            Spec("Hardware", HostPlatform.IsContainer ? "GPU devices in the container" : "GPU devices",
                hw.RenderNodes.Count == 0 && !hw.NvidiaDevice ? "None" : string.Join(", ", hw.RenderNodes.Concat(hw.NvidiaDevice ? new[] { "/dev/nvidia0" } : Array.Empty<string>())));
        }
        else
        {
            Spec("Hardware", "GPU", $"Not checked on {HostPlatform.Label} (see Hardware acceleration)");
        }
        Spec("Hardware", "Hardware acceleration", Nice(SettingsReader.Text(encoding, "HardwareAccelerationType")) ?? "unknown");

        SpaceSpec("Storage", "Config & database drive", _paths.DataPath);
        SpaceSpec("Storage", "Cache drive (images, metadata)", _paths.CachePath);
        string transcodePath = TranscodePath(encoding);
        SpaceSpec("Storage", $"Transcode folder ({transcodePath})", transcodePath);

        long db = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db"));
        long wal = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db-wal"));
        if (db > 0)
        {
            Spec("Storage", "Database size", $"{SystemProbe.Size(db)} (+ {SystemProbe.Size(wal)} write-ahead log)");
        }

        Spec("Storage", "Log folder size", SystemProbe.Size(SystemProbe.DirectorySize(_paths.LogDirectoryPath)));

        // Every library drive, and how fast each drive is filling.
        foreach (var d in WatchedDrives(libraries, encoding))
        {
            string rate = d.GbPerDay switch
            {
                null => "fill rate known after 3 days",
                <= 0.05 => "not filling up",
                { } r => $"using about {SystemProbe.Gb(r)} a day" + (d.DaysToFull is { } days ? $", full in {Weeks(days)}" : string.Empty)
            };
            Spec("Storage", $"Drive for {d.Holds}", $"{SystemProbe.Gb(d.FreeGb)} free of {SystemProbe.Gb(d.TotalGb)} ({rate})");
        }

        // Folders that quietly grow. Measured in the background, as they can hold millions of files.
        var serverPaths = _paths as IServerApplicationPaths;
        foreach (var (label, path) in new[]
                 {
                     ("Metadata folder", serverPaths?.InternalMetadataPath),
                     ("Trickplay images", _paths.TrickplayPath),
                     ("Image cache", _paths.ImageCachePath),
                     ("Cache folder (all)", _paths.CachePath)
                 })
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                continue;
            }

            Spec("Storage", label, StorageWatch.FolderSize(path) is { } size
                ? SystemProbe.Size(size.Bytes) + (size.Complete ? string.Empty : " or more")
                : "measuring, check back in a few minutes");
        }

        Spec("Libraries", "Libraries", libraries.Count.ToString(CultureInfo.InvariantCulture));
        long total = libraries.Sum(l => l.ItemCount ?? 0);
        Spec("Libraries", "Total items", total.ToString("N0", CultureInfo.InvariantCulture));
        foreach (var lib in libraries)
        {
            string count = lib.ItemCount is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) + " items" : "count unavailable";
            Spec("Libraries", lib.Name, count + (lib.IsStreamed ? " (mostly .strm streams)" : string.Empty));
        }

        Spec("Other", "Plugins installed", pluginCount.ToString(CultureInfo.InvariantCulture));
        Spec("Other", "Scheduled tasks", _tasks.ScheduledTasks.Count.ToString(CultureInfo.InvariantCulture));
    }

    // ---------- Hardware & transcoding ----------

    private void CheckTranscoding(HardwareInfo hw, object? enc)
    {
        if (enc is null)
        {
            Add(AreaHardware, Sev.Tip, "Couldn't read your transcoding settings", "Unknown", string.Empty,
                "Jellyfin didn't return its transcoding configuration, so these checks were skipped.", WhereTranscoding);
            return;
        }

        string hwType = (SettingsReader.Text(enc, "HardwareAccelerationType") ?? "none").Trim().ToLowerInvariant();
        bool hwOn = hwType is not ("" or "none");

        (string Value, string Label) suggested = hw.GpuVendor switch
        {
            "intel" => ("qsv", "Intel QuickSync (QSV)"),
            "amd" => ("vaapi", "Video Acceleration API (VAAPI)"),
            "nvidia" => ("nvenc", "NVIDIA NVENC"),
            _ => (string.Empty, string.Empty)
        };

        if (!HostPlatform.IsLinux)
        {
            // Windows and macOS don't expose the GPU the way Linux does, so Medic can't see which one you
            // have. Point at the setting instead of guessing.
            if (!hwOn)
            {
                Add(AreaHardware, Sev.Tip, "Hardware acceleration is off", "None (everything on the CPU)",
                    HostPlatform.GpuSetup,
                    "If this computer has a graphics card or built-in graphics, Jellyfin can use it for transcodes, chapter images and trickplay instead of the CPU.",
                    WhereTranscoding);
            }
        }
        else if (!hwOn && suggested.Value.Length > 0)
        {
            Add(AreaHardware, Sev.Problem, "Your GPU isn't being used",
                "None (everything on the CPU)",
                suggested.Label,
                $"{hw.GpuDescription} is available to Jellyfin, but hardware acceleration is off. Every transcode, plus chapter and trickplay images, is being done by the CPU.",
                WhereTranscoding);
        }
        else if (!hwOn)
        {
            Add(AreaHardware, hw.CpuThreads <= 8 ? Sev.Improve : Sev.Tip, "No GPU available to Jellyfin",
                $"CPU only, {hw.CpuThreads} threads",
                HostPlatform.GpuSetup,
                "Without a GPU, each 1080p transcode can take several CPU threads, and 4K or HDR transcodes may stutter. If the server has integrated graphics, using it costs nothing.",
                WhereDocker);
        }
        else
        {
            bool deviceThere = !HostPlatform.IsLinux || hwType switch
            {
                "nvenc" => hw.NvidiaDevice,
                "qsv" or "vaapi" => hw.RenderNodes.Count > 0,
                _ => true
            };

            if (!deviceThere)
            {
                Add(AreaHardware, Sev.Problem,
                    HostPlatform.IsContainer ? "Hardware acceleration is on, but the GPU isn't in the container" : "Hardware acceleration is on, but Jellyfin can't see the GPU",
                    Nice(hwType) ?? hwType, HostPlatform.GpuSetup + ", or set hardware acceleration to None",
                    "Jellyfin will try to use a device that isn't there, so transcodes fail or fall back to the CPU.",
                    WhereDocker);
            }
            else if (HostPlatform.IsLinux && suggested.Value.Length > 0 && hwType != suggested.Value && !(hw.GpuVendor == "intel" && hwType == "vaapi"))
            {
                Add(AreaHardware, Sev.Tip, "Hardware acceleration type doesn't match your GPU",
                    Nice(hwType) ?? hwType, suggested.Label,
                    $"You have {hw.GpuDescription}; {suggested.Label} is the method designed for it.",
                    WhereTranscoding);
            }

            if (SettingsReader.Bool(enc, "EnableHardwareEncoding") == false)
            {
                Add(AreaHardware, Sev.Improve, "Hardware encoding is off", "Off", "On",
                    "The GPU decodes video but the CPU still does the heavier encoding step.", WhereTranscoding);
            }

            var codecs = SettingsReader.List(enc, "HardwareDecodingCodecs");
            if (codecs is not null && codecs.Count == 0)
            {
                Add(AreaHardware, Sev.Improve, "No formats are ticked for hardware decoding", "None ticked",
                    "Tick H264 and HEVC, plus VP9 and AV1 if your GPU supports them",
                    "With nothing ticked, the CPU decodes every video even though the GPU could.", WhereTranscoding);
            }
            else if (codecs is not null && deviceThere)
            {
                var missing = new[] { "h264", "hevc" }.Where(c => !codecs.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                {
                    Add(AreaHardware, Sev.Improve, $"{string.Join(" and ", missing.Select(c => c.ToUpperInvariant()))} aren't decoded on the GPU",
                        "Ticked: " + string.Join(", ", codecs.Select(c => c.ToUpperInvariant())), "Tick H264 and HEVC",
                        "Nearly all films and series are H264 or HEVC. Unticked, the CPU decodes them before the GPU encodes, which is the slow half of a transcode.", WhereTranscoding);
                }
            }

            if (deviceThere && SettingsReader.Bool(enc, "EnableDecodingColorDepth10Hevc") == false)
            {
                Add(AreaHardware, Sev.Improve, "10-bit HEVC is decoded on the CPU", "Off", "Enable 10-bit hardware decoding for HEVC: on",
                    "Most HDR and many recent films are 10-bit HEVC. Decoding them on the CPU is heavy, and 4K HDR can stutter. Every GPU Jellyfin supports from the last several years can do it.", WhereTranscoding);
            }

            if (deviceThere && SettingsReader.Bool(enc, "AllowHevcEncoding") == false && SettingsReader.Bool(enc, "EnableHardwareEncoding") != false)
            {
                Add(AreaHardware, Sev.Tip, "Transcodes are only made as H264", "HEVC encoding off", "Allow encoding in HEVC format: on",
                    "Apps that can play HEVC get the same picture at about half the bitrate, which helps remote viewers on slow connections. Apps that can't still get H264.", WhereTranscoding);
            }

            if (deviceThere && hw.GpuVendor == "intel" && hwType is "qsv" or "vaapi"
                && SettingsReader.Bool(enc, "EnableIntelLowPowerH264HwEncoder") == false
                && SettingsReader.Bool(enc, "EnableIntelLowPowerHevcHwEncoder") == false)
            {
                Add(AreaHardware, Sev.Tip, "Intel low-power encoding is off", "Off", "Enable Intel Low-Power H.264 and HEVC encoders: on",
                    "On 8th-generation Intel and later, the low-power encoder is faster and leaves the rest of the GPU free for tone mapping and more streams. It needs the GuC/HuC firmware on the host, which most modern Linux systems load by default.", WhereTranscoding);
            }

            // Trickplay images are made with the same FFmpeg, and can use the GPU too.
            var server = (object)_config.Configuration;
            // (Trickplay on the CPU altogether is checked under Server.)
            if (SettingsReader.Bool(server, "TrickplayOptions.EnableHwAcceleration") != false
                && SettingsReader.Bool(server, "TrickplayOptions.EnableHwEncoding") == false)
            {
                Add(AreaHardware, Sev.Tip, "Trickplay images are encoded on the CPU", "Hardware encoding for trickplay: off", "On (if your GPU supports MJPEG encoding)",
                    "Intel and recent AMD GPUs can also encode the thumbnails, which takes the last part of the work off the CPU.", WhereTrickplay);
            }
        }

        string preset = (SettingsReader.Text(enc, "EncoderPreset") ?? string.Empty).Trim().ToLowerInvariant();
        if (!hwOn && preset is "slow" or "slower" or "veryslow")
        {
            Add(AreaHardware, Sev.Improve, "Software transcodes use a slow preset", preset, "Auto, or veryfast",
                "With no GPU doing the work, a slow preset makes each transcode use far more CPU for a small gain in quality, so fewer people can watch at once.", WhereTranscoding);
        }

        if (!hwOn && SettingsReader.Bool((object)_config.Configuration, "TrickplayOptions.EnableKeyFrameOnlyExtraction") == false)
        {
            Add(AreaHardware, Sev.Tip, "Trickplay reads every frame", "Key frames only: off", "On",
                "Without a GPU, making trickplay images from key frames only is many times faster. The thumbnails are a little less exact.", WhereTrickplay);
        }

        bool? toneMapping = SettingsReader.Bool(enc, "EnableTonemapping");
        if (!hwOn && toneMapping == true)
        {
            Add(AreaHardware, Sev.Improve, "Tone mapping is on without a GPU", "On (CPU)", "Off, or add a GPU",
                "Converting HDR to SDR on the CPU is very heavy and can make HDR transcodes unwatchable.", WhereTranscoding);
        }
        else if (hwOn && toneMapping == false)
        {
            Add(AreaHardware, Sev.Tip, "Tone mapping is off", "Off", "On",
                "HDR films transcoded for non-HDR screens look washed out without it, and your GPU can do it cheaply.", WhereTranscoding);
        }

        if (SettingsReader.Bool(enc, "EnableThrottling") == false)
        {
            Add(AreaHardware, Sev.Tip, "Transcode throttling is off", "Off", "On",
                "Throttling pauses a transcode once it's far enough ahead of playback, which saves CPU/GPU time. Turn it back off if a particular device has trouble.",
                WhereTranscoding);
        }

        string transcodePath = TranscodePath(enc);
        var mount = SystemProbe.Mount(transcodePath);
        var space = SystemProbe.Space(transcodePath);
        bool inRam = mount?.FsType == "tmpfs";

        if (SettingsReader.Bool(enc, "EnableSegmentDeletion") == false)
        {
            Add(AreaHardware, inRam ? Sev.Improve : Sev.Tip, "Old transcode segments aren't deleted", "Off", "On",
                inRam ? "Your transcode folder is in RAM, so leftover segments eat memory until the transcode ends."
                      : "Leftover segments build up on disk while long films transcode.",
                WhereTranscoding);
        }

        long? threads = SettingsReader.Number(enc, "EncodingThreadCount");
        if (threads > hw.CpuThreads)
        {
            Add(AreaHardware, Sev.Improve, "Transcoding is set to use more threads than you have",
                threads.Value.ToString(CultureInfo.InvariantCulture), "Auto (-1)",
                $"Only {hw.CpuThreads} threads are available to Jellyfin, so the extra threads just compete with each other.",
                WhereTranscoding);
        }

        string? ffmpeg = SettingsReader.Text(enc, "EncoderAppPathDisplay") ?? SettingsReader.Text(enc, "EncoderAppPath");
        if (!string.IsNullOrWhiteSpace(ffmpeg) && !File.Exists(ffmpeg))
        {
            Add(AreaHardware, Sev.Problem, "FFmpeg wasn't found", ffmpeg, "The path to Jellyfin's bundled FFmpeg",
                "Without FFmpeg, nothing can be transcoded and no images can be extracted.", WhereTranscoding);
        }

        if (inRam)
        {
            if (space is { } ram && ram.TotalGb < 2)
            {
                Add(AreaHardware, Sev.Problem, "Your transcode folder is in RAM but very small",
                    $"{SystemProbe.Gb(ram.TotalGb)} available",
                    "At least 4 GB: " + HostPlatform.BiggerRamFolder,
                    HostPlatform.IsDockerLike
                        ? "Docker gives /dev/shm only 64 MB by default. Transcodes fail part-way through when it fills up."
                        : "Transcodes fail part-way through when the RAM folder fills up.",
                    WhereDocker);
            }
        }
        else
        {
            if (mount?.FsType.StartsWith("fuse.shfs", StringComparison.Ordinal) == true)
            {
                Add(AreaHardware, Sev.Improve, "Transcodes go through Unraid's user-share layer",
                    $"{transcodePath} (on /mnt/user)", "A cache-pool path (/mnt/cache/...) or RAM",
                    "The user-share layer (FUSE) adds overhead to every write and can wake array disks.", WhereDocker);
            }

            if (space is { } disk && disk.FreeGb < 10)
            {
                Add(AreaHardware, Sev.Improve, "Low space for transcodes", $"{SystemProbe.Gb(disk.FreeGb)} free", "At least 10 GB free",
                    "A 4K transcode can use several GB of temporary space; when it runs out, playback stops.", WhereTranscoding);
            }

            if (hw.MemoryGb >= 16 && HostPlatform.RamTranscodeHow is { } how)
            {
                Add(AreaHardware, Sev.Tip, "You could transcode to RAM", transcodePath, how,
                    $"Jellyfin can use {SystemProbe.Gb(hw.MemoryGb)} of memory. Transcoding to RAM saves SSD wear and is slightly faster. This is optional: "
                        + (HostPlatform.Kind == HostKind.Unraid ? "an SSD cache pool is perfectly fine." : "an SSD is perfectly fine too."),
                    WhereDocker + ", then Dashboard → Playback → Transcoding");
            }
        }
    }

    // ---------- Storage ----------

    private List<DriveStatus>? _drives;

    /// <summary>Config, cache, transcode and library drives, each once, with today's reading saved.</summary>
    private List<DriveStatus> WatchedDrives(List<LibraryFacts> libraries, object? encoding)
    {
        if (_drives is not null)
        {
            return _drives;
        }

        var folders = new List<(string, string)>
        {
            ("config & database", _paths.DataPath),
            ("cache", _paths.CachePath),
            ("transcodes", TranscodePath(encoding))
        };
        folders.AddRange(libraries.Where(l => !l.IsStreamed).SelectMany(l => l.Locations.Select(loc => (l.Name, loc))));
        _drives = StorageWatch.Drives(folders);
        StorageWatch.Record(_paths.PluginConfigurationsPath, _drives);
        return _drives;
    }

    private static string Weeks(double days) =>
        days < 14 ? $"about {Math.Max(1, Math.Round(days)):0} days" : days < 120 ? $"about {Math.Round(days / 7):0} weeks" : $"about {Math.Round(days / 30):0} months";

    private void CheckStorage(List<LibraryFacts> libraries, object? encoding)
    {
        foreach (var d in WatchedDrives(libraries, encoding))
        {
            bool holdsConfig = d.Holds.Contains("config & database", StringComparison.Ordinal) || d.Holds.Contains("cache", StringComparison.Ordinal);
            double freePct = d.TotalGb > 0 ? d.FreeGb / d.TotalGb * 100 : 100;
            if (!holdsConfig && (freePct < 1 || d.FreeGb < 5))
            {
                Add(AreaStorage, Sev.Problem, $"The drive for {d.Holds} is full", $"{SystemProbe.Gb(d.FreeGb)} free of {SystemProbe.Gb(d.TotalGb)}",
                    "Free some space or add a drive", "New downloads and track cleanup will fail, and Jellyfin can't save images or subtitles next to your files.", d.ExamplePath);
            }
            else if (!holdsConfig && (freePct < 3 || d.FreeGb < 25))
            {
                Add(AreaStorage, Sev.Improve, $"The drive for {d.Holds} is nearly full", $"{SystemProbe.Gb(d.FreeGb)} free of {SystemProbe.Gb(d.TotalGb)}",
                    "Keep at least 3% or 25 GB free", "A full drive stops new files arriving and can stop Jellyfin saving artwork and subtitles.", d.ExamplePath);
            }

            if (d.DaysToFull is { } days && days < 30)
            {
                Add(AreaStorage, days < 7 ? Sev.Problem : Sev.Improve, $"The drive for {d.Holds} will be full in {Weeks(days)}",
                    $"{SystemProbe.Gb(d.FreeGb)} free, using about {SystemProbe.Gb(d.GbPerDay!.Value)} a day",
                    "Free some space, add a drive, or find what's filling it (for example trickplay images or old downloads)",
                    "At this rate the drive runs out soon, and Jellyfin, downloads and track cleanup all need room to write.", d.ExamplePath);
            }
        }

        if (SystemProbe.Space(_paths.DataPath) is { } data)
        {
            if (data.FreeGb < 5)
            {
                Add(AreaStorage, Sev.Problem, "Almost no space left for the database", $"{SystemProbe.Gb(data.FreeGb)} free",
                    "Free up space, or move " + HostPlatform.DataFolder + " to a bigger drive", "If the database can't write, Jellyfin can corrupt it or stop working.", WhereDocker);
            }
            else if (data.FreeGb < 15)
            {
                Add(AreaStorage, Sev.Improve, "Low space on the config drive", $"{SystemProbe.Gb(data.FreeGb)} free",
                    "At least 15 GB free", "Metadata, images and database growth all land here.", WhereDocker);
            }
        }

        if (SystemProbe.Mount(_paths.DataPath) is { } dataMount && dataMount.FsType.StartsWith("fuse.shfs", StringComparison.Ordinal))
        {
            Add(AreaStorage, Sev.Improve, "Jellyfin's database goes through Unraid's user-share layer",
                "/config mapped to /mnt/user/appdata/...",
                "Map /config to /mnt/cache/appdata/... (or make appdata an exclusive share)",
                "The database does thousands of small reads and writes. The user-share layer (FUSE) slows each one down, which makes the web UI and library scans noticeably slower.",
                WhereDocker);
        }

        if (!SameDrive(_paths.CachePath, _paths.DataPath) && SystemProbe.Space(_paths.CachePath) is { } cache && cache.FreeGb < 10)
        {
            Add(AreaStorage, Sev.Improve, "Low space on the cache drive", $"{SystemProbe.Gb(cache.FreeGb)} free", "At least 10 GB free",
                "Images and metadata are cached here; when it's full, artwork stops loading.", WhereDocker);
        }

        long wal = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db-wal"));
        if (wal > 512L * 1024 * 1024)
        {
            Add(AreaStorage, Sev.Improve, "The database's write-ahead log is large", SystemProbe.Size(wal),
                "Run the \"Optimize database\" task, and schedule it weekly",
                "A large log slows database reads until it's merged back in.", WhereTasks);
        }

        long logs = SystemProbe.DirectorySize(_paths.LogDirectoryPath);
        if (logs > 1024L * 1024 * 1024)
        {
            Add(AreaStorage, Sev.Improve, "Log files are taking a lot of space", SystemProbe.Size(logs),
                "Under 1 GB: delete old logs and check the log level below",
                "Big logs usually mean something is logging errors repeatedly, or debug logging is on.", "Dashboard → Logs");
        }

        string? level = LogLevel();
        if (level is "Debug" or "Verbose")
        {
            Add(AreaStorage, Sev.Improve, "Debug logging is on", level, "Information",
                "Debug logging writes far more and slows the server slightly. Only turn it on while troubleshooting.",
                "logging.json in your Jellyfin config folder");
        }
    }

    // ---------- Themes (custom CSS) ----------

    private string CustomCss() => SettingsReader.Text(TryConfig("branding"), "CustomCss") ?? string.Empty;

    private static readonly System.Text.RegularExpressions.Regex ImportRule = new(
        @"@import\s+(?:url\(\s*)?[""']?([^""')\s;]+)[""']?\s*\)?[^;]*;?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex Comments = new(@"/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>Each @import in the CSS, with whether it comes after other rules (browsers ignore those).</summary>
    public static List<(string Url, bool Late)> ThemeImports(string css)
    {
        string text = Comments.Replace(css ?? string.Empty, string.Empty);
        var found = new List<(string, bool)>();
        foreach (System.Text.RegularExpressions.Match m in ImportRule.Matches(text))
        {
            // Only @charset and other @imports may come before an @import.
            string before = ImportRule.Replace(text[..m.Index], string.Empty);
            before = System.Text.RegularExpressions.Regex.Replace(before, @"@charset[^;]*;", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            found.Add((m.Groups[1].Value.Trim(), before.Trim().Length > 0));
        }

        return found;
    }

    private void CheckTheme()
    {
        string css = CustomCss();
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        var imports = ThemeImports(css);
        foreach (var (url, late) in imports)
        {
            if (late)
            {
                Add(AreaTheme, Sev.Improve, "A theme import in your custom CSS is ignored", url,
                    "Move every @import line to the very top of the custom CSS",
                    "Browsers skip an @import that comes after any other rule, so this theme never loads. It's the most common reason a theme \"stops working\" after adding a tweak above it.",
                    WhereCustomCss);
            }

            if (url.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                Add(AreaTheme, Sev.Improve, "A theme is loaded from raw.githubusercontent.com", url,
                    "Use the theme's jsDelivr or GitHub Pages address from its install page instead",
                    "GitHub serves these files as plain text and tells browsers not to guess, so browsers refuse to use them as a stylesheet and the theme doesn't apply.",
                    WhereCustomCss);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                Add(AreaTheme, Sev.Tip, "A theme is loaded over plain http", url, "The https:// address",
                    "When you open Jellyfin over https (for example through a reverse proxy), browsers block http stylesheets, so the theme only works on some devices.",
                    WhereCustomCss);
            }
        }

        string code = Comments.Replace(css, string.Empty);
        int open = code.Count(c => c == '{');
        int close = code.Count(c => c == '}');
        if (open != close)
        {
            Add(AreaTheme, Sev.Improve, "Your custom CSS has unbalanced braces", $"{open} opening {{ and {close} closing }}",
                "Find the rule missing a brace (usually the last one you added)",
                "Browsers drop everything from the broken rule onwards, so parts of your theme or tweaks silently stop applying, and pages can draw oddly.",
                WhereCustomCss);
        }

        if (css.Contains("/*", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.Matches(css, @"/\*").Count > System.Text.RegularExpressions.Regex.Matches(css, @"\*/").Count)
        {
            Add(AreaTheme, Sev.Improve, "A comment in your custom CSS is never closed", "/* without */",
                "Close the comment with */",
                "Everything after an unclosed comment is ignored, including any theme or tweaks below it.",
                WhereCustomCss);
        }
    }

    // ---------- Serious errors (also shown as home-page banners) ----------

    private void CheckCriticalErrors()
    {
        foreach (var alert in AdminAlerts.CriticalFromLogs(_paths.LogDirectoryPath))
        {
            int split = alert.Detail.IndexOf(" Last seen: ", StringComparison.Ordinal);
            string advice = split > 0 ? alert.Detail[..split] : alert.Detail;
            string seen = split > 0 ? alert.Detail[(split + 12)..] : string.Empty;
            Add("Logs", Sev.Problem, alert.Title, seen, advice,
                "Jellyfin logged this as an error. It's shown however rarely it happens, as it can mean data loss or a server that stops working.",
                "Dashboard → Logs");
        }
    }

    // ---------- Resource spikes ----------

    private void CheckResourceSpikes()
    {
        var recent = ResourceLog.Load(_paths).Where(e => e.TimeUtc > DateTime.UtcNow.AddDays(-7)).ToList();
        if (recent.Count == 0)
        {
            return;
        }

        // The causes seen most often, without their percentages, so the same task counts once.
        var top = recent.SelectMany(e => e.Causes)
            .Select(c => System.Text.RegularExpressions.Regex.Replace(c, @" \(\d+%\)$", string.Empty))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()}×)");
        var kinds = recent.GroupBy(e => e.Resource).Select(g => $"{g.Key} {g.Count()}×");
        Add(AreaServer, recent.Count >= 10 ? Sev.Improve : Sev.Tip, $"Jellyfin ran flat out {recent.Count} time{(recent.Count == 1 ? string.Empty : "s")} this week",
            string.Join(", ", kinds) + ". Most often running: " + string.Join("; ", top),
            "Move the tasks named here to quieter times (Schedule), or let the GPU take the transcodes",
            "Each time, Jellyfin and its FFmpeg processes used nearly all of a resource for at least 45 seconds, which is when playback buffers and pages load slowly. Medic → Dashboard lists each one and what was running.",
            "Medic → Dashboard");
    }

    // ---------- General performance ----------

    private void CheckPerformance(object server, HardwareInfo hw, int pluginCount)
    {
        if (SystemProbe.Spinning(_paths.DataPath) == true)
        {
            Add(AreaServer, Sev.Improve, "Jellyfin's database is on a spinning hard drive", HostPlatform.DataFolder + " is on a hard drive",
                "Move it to an SSD (on Unraid, the cache pool)",
                "The database does thousands of small reads and writes. On a hard drive every page of the web UI, every scan and every \"continue watching\" waits for the disk. An SSD makes Jellyfin feel several times quicker.",
                WhereDocker);
        }

        long db = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db"));
        if (db > 4L * 1024 * 1024 * 1024)
        {
            Add(AreaServer, Sev.Tip, "The database is very large", SystemProbe.Size(db),
                "Keep the activity log for 30–90 days, remove libraries you no longer use, and let \"Optimize database\" run weekly",
                "A big database makes scans, searches and the home screen slower. Old activity entries and libraries of channels nobody watches are the usual causes.",
                WhereTasks);
        }

        if (StorageWatch.FolderSize(_paths.ImageCachePath) is { } images && images.Bytes > 30L * 1024 * 1024 * 1024)
        {
            Add(AreaServer, Sev.Tip, "The image cache is very large", SystemProbe.Size(images.Bytes) + (images.Complete ? string.Empty : " or more"),
                "Run \"Clean Cache Directory\", and keep the cache on an SSD",
                "Resized artwork piles up here. It's rebuilt as needed, so clearing it is safe, and on a slow disk a huge cache makes artwork load slowly.",
                WhereTasks);
        }

        long? imageLimit = SettingsReader.Number(server, "ParallelImageEncodingLimit");
        if (imageLimit is 0 && hw.CpuThreads is > 0 and <= 4)
        {
            Add(AreaServer, Sev.Tip, "Image resizing can use every CPU thread", "Unlimited", "2",
                $"With {hw.CpuThreads} CPU threads, a page full of new artwork can make playback stutter while images are resized. A limit of 2 keeps a thread free.",
                WhereGeneral);
        }

        if (pluginCount >= 30)
        {
            Add(AreaServer, Sev.Tip, "Lots of plugins are installed", pluginCount.ToString(CultureInfo.InvariantCulture), "Only the ones you use",
                "Each plugin loads at start-up and many run their own background work and scheduled tasks. Medic → Plugin directory lists ones you probably don't need any more.",
                "Dashboard → Plugins");
        }
    }

    // ---------- Server settings ----------

    private void CheckServer(object server, HardwareInfo hw, List<LibraryFacts> libraries)
    {
        long? bitrate = SettingsReader.Number(server, "RemoteClientBitrateLimit");
        if (bitrate > 0 && bitrate < 4_000_000)
        {
            Add(AreaServer, Sev.Improve, "Remote streaming is capped very low", $"{bitrate / 1_000_000d:0.#} Mbps",
                "At least 8 Mbps, or match your upload speed",
                "Below about 4 Mbps, almost everything watched away from home has to be transcoded, which loads the server and lowers quality.",
                "Dashboard → Playback → Streaming");
        }

        CheckBitrateAgainstSpeedTest(bitrate ?? 0);

        if (hw.InDocker && string.IsNullOrWhiteSpace(_config.Configuration.ServerName))
        {
            Add(AreaServer, Sev.Tip, "The server has no name", "(not set)", "A name such as \"Tower Jellyfin\"",
                "In Docker an unnamed server shows the container's random ID in apps, which also changes if the container is recreated.",
                WhereGeneral);
        }

        if (SettingsReader.TryGet(server, "ActivityLogRetentionDays", out var retention) && (SettingsReader.ToNumber(retention) is null or <= 0))
        {
            Add(AreaServer, Sev.Tip, "The activity log is kept forever", "Forever", "30–90 days",
                "Every playback and login is recorded; over years this grows the database for no benefit.", WhereGeneral);
        }

        foreach (var (setting, label) in new[]
                 {
                     ("LibraryScanFanoutConcurrency", "Parallel library scan tasks"),
                     ("LibraryMetadataRefreshConcurrency", "Parallel metadata refresh tasks"),
                     ("ParallelImageEncodingLimit", "Parallel image encoding")
                 })
        {
            long? value = SettingsReader.Number(server, setting);
            if (value > hw.CpuThreads)
            {
                Add(AreaServer, Sev.Improve, $"{label} is set higher than your CPU threads", value.Value.ToString(CultureInfo.InvariantCulture),
                    "0 (automatic) or no more than " + hw.CpuThreads,
                    "More parallel work than threads makes everything slower and can lock up the web UI during scans.",
                    "Dashboard → General → Performance");
            }
        }

        long totalItems = libraries.Sum(l => l.ItemCount ?? 0);
        long? monitorDelay = SettingsReader.Number(server, "LibraryMonitorDelay");
        if (totalItems >= 10_000 && monitorDelay < 60)
        {
            Add(AreaServer, Sev.Tip, "Library changes are picked up very quickly", $"{monitorDelay} seconds", "120 seconds",
                "With a library your size, a short delay means bulk changes (like a playlist update) trigger many small scans instead of one.",
                "Dashboard → Libraries → Display or Advanced");
        }

        bool trickplayUsed = libraries.Any(l => SettingsReader.Bool(l.Options, "EnableTrickplayImageExtraction") == true);
        if (trickplayUsed)
        {
            bool gpu = hw.GpuVendor is "intel" or "amd" or "nvidia";
            if (gpu && SettingsReader.Bool(server, "TrickplayOptions.EnableHwAcceleration") == false)
            {
                Add(AreaServer, Sev.Improve, "Trickplay images are made on the CPU", "Hardware acceleration off", "On",
                    "Generating seek-bar previews is very heavy; your GPU can do it many times faster.",
                    "Dashboard → Playback → Trickplay");
            }

            long? tpThreads = SettingsReader.Number(server, "TrickplayOptions.ProcessThreads");
            if (tpThreads > hw.CpuThreads)
            {
                Add(AreaServer, Sev.Improve, "Trickplay is set to use more threads than you have", tpThreads.Value.ToString(CultureInfo.InvariantCulture),
                    $"No more than {Math.Max(1, hw.CpuThreads / 2)}", "It competes with playback for CPU time.",
                    "Dashboard → Playback → Trickplay");
            }
        }
    }

    /// <summary>
    /// Compares the remote streaming limit with the last internet speed test (Tests tab),
    /// assuming two people watching away from home at once.
    /// </summary>
    private void CheckBitrateAgainstSpeedTest(long currentBps)
    {
        var test = SpeedStore.Load(_paths);
        if (test is null || test.UploadMbps <= 0 || test.TestedUtc < DateTime.UtcNow.AddDays(-90))
        {
            return;
        }

        var option = SpeedAdvice.Options(test.UploadMbps).First(o => o.Viewers == 2);
        double currentMbps = currentBps / 1_000_000d;
        string current = currentBps <= 0 ? "No limit" : $"{currentMbps:0.#} Mbps";
        string recommended = option.RecommendedLimitMbps == 0 ? "No limit needed" : $"{option.RecommendedLimitMbps} Mbps";
        string why = $"Your server's upload measured {test.UploadMbps:0.#} Mbps on {test.TestedUtc.ToLocalTime():d MMM}. With two people watching away from home, each gets about {option.PerViewerMbps:0.#} Mbps: {option.Quality.ToLowerInvariant()}. Change the number of viewers on the Tests tab.";
        const string where = "Dashboard → Playback → Streaming → Internet streaming bitrate limit";

        if (option.RecommendedLimitMbps == 0)
        {
            return;
        }

        if (currentBps <= 0)
        {
            Add(AreaServer, Sev.Improve, "No remote streaming limit, but your upload can't keep up with full quality", current, recommended,
                why + " Without a limit, apps can ask for more than your connection can send, which causes buffering.", where);
        }
        else if (currentMbps > option.RecommendedLimitMbps * 1.25)
        {
            Add(AreaServer, Sev.Improve, "The remote streaming limit is higher than your upload can deliver", current, recommended, why, where);
        }
        else if (currentMbps < option.RecommendedLimitMbps * 0.6)
        {
            Add(AreaServer, Sev.Tip, "You could allow better quality away from home", current, recommended,
                why + " Your current limit transcodes more than it needs to.", where);
        }
    }

    // ---------- Libraries ----------

    private void CheckLibraries(List<LibraryFacts> libraries)
    {
        foreach (var lib in libraries)
        {
            foreach (var location in lib.Locations.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                if (!Directory.Exists(location))
                {
                    Add(AreaLibraries, Sev.Problem, $"{lib.Name}: a library folder can't be found", location,
                        HostPlatform.IsContainer
                            ? "Check the folder is mapped into the container and the drive is mounted"
                            : "Check the drive is connected and mounted, and that Jellyfin can read the folder",
                        "Jellyfin can't see anything in that folder, and a scan may mark its items as missing.", WhereDocker);
                }
            }

            if (lib.IsStreamed && lib.ItemCount >= 50_000)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: a very large IPTV library", $"{lib.ItemCount:N0} items",
                    "Import only the categories you actually watch",
                    "Every list, search and home screen row has to work through these items. Library size is the biggest single factor in how fast this server feels.",
                    "Your IPTV plugin's settings (for example Xtream Library's categories)");
            }

            var opts = lib.Options;
            if (opts is null)
            {
                continue;
            }

            if (lib.IsStreamed)
            {
                if (SettingsReader.Bool(opts, "EnableChapterImageExtraction") == true)
                {
                    Add(AreaLibraries, Sev.Improve, $"{lib.Name}: chapter images are on for a streamed library", "On", "Off",
                        "This library is mostly .strm links to online streams. Extracting images means opening each stream, which is slow and can trip your provider's connection limits.",
                        WhereLibraries);
                }

                if (SettingsReader.Bool(opts, "EnableTrickplayImageExtraction") == true)
                {
                    Add(AreaLibraries, Sev.Improve, $"{lib.Name}: trickplay is on for a streamed library", "On", "Off",
                        "Trickplay would have to read every online stream end to end.", WhereLibraries);
                }

                if (SettingsReader.Bool(opts, "EnableRealtimeMonitor") == true)
                {
                    Add(AreaLibraries, Sev.Tip, $"{lib.Name}: real-time monitoring is on", "On", "Off",
                        "The plugin that writes these .strm files changes many at once; a scheduled scan picks them up more efficiently than thousands of file-change events.",
                        WhereLibraries);
                }
            }
            else if (lib.ItemCount >= 5_000 && SettingsReader.Bool(opts, "EnableRealtimeMonitor") == true)
            {
                string type = (lib.CollectionType ?? string.Empty).ToLowerInvariant();
                string arr = type == "tvshows" ? "Sonarr" : type == "movies" ? "Radarr" : string.Empty;
                string unraidNote = HostPlatform.Kind == HostKind.Unraid ? ", and on Unraid user shares change events aren't always reliable" : string.Empty;
                if (arr.Length > 0)
                {
                    Add(AreaLibraries, Sev.Tip, $"{lib.Name}: real-time monitoring on a large library", "On",
                        $"Off, with {arr} set to notify Jellyfin, and a daily scan",
                        $"Watching a large library takes a system file watcher for every folder{unraidNote}. {arr}'s notifications update Jellyfin instantly and precisely.",
                        WhereLibraries + $"; {arr} → Settings → Connect → Emby / Jellyfin");
                }
                else
                {
                    Add(AreaLibraries, Sev.Tip, $"{lib.Name}: real-time monitoring on a large library", "On",
                        "Off, with a daily library scan",
                        $"Watching a large library takes a system file watcher for every folder{unraidNote}. A daily scan picks up new files reliably.",
                        WhereLibraries);
                }
            }

            if (SettingsReader.Bool(opts, "ExtractChapterImagesDuringLibraryScan") == true)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: chapter images are extracted during scans", "On", "Off",
                    "This makes every library scan much longer. The nightly \"Extract Chapter Images\" task does the same job off-peak.",
                    WhereLibraries);
            }

            if (SettingsReader.Bool(opts, "ExtractTrickplayImagesDuringLibraryScan") == true)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: trickplay images are made during scans", "On", "Off",
                    "Scans take far longer. Let the scheduled trickplay task do it overnight instead.", WhereLibraries);
            }

            long? refreshDays = SettingsReader.Number(opts, "AutomaticRefreshIntervalDays");
            if (refreshDays is > 0 and < 30 && lib.ItemCount >= 2_000)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: metadata is re-downloaded every {refreshDays} days", $"Every {refreshDays} days",
                    "Never, or every 90 days",
                    $"With {lib.ItemCount:N0} items, that's thousands of lookups a day to TMDB and others, slowing the server and risking rate limits.",
                    WhereLibraries);
            }

            if (SettingsReader.Bool(opts, "EnableLUFSScan") == true && lib.ItemCount >= 5_000)
            {
                Add(AreaLibraries, Sev.Tip, $"{lib.Name}: audio normalisation scanning is on", "On", "Off unless you use it",
                    "Measuring loudness reads every audio file in full, which is slow on a large library.", WhereLibraries);
            }
        }
    }

    // ---------- Live TV ----------

    private void CheckLiveTv(object? liveTv)
    {
        CheckOrphanChannels();

        long? guideDays = SettingsReader.Number(liveTv, "GuideDays");
        if (guideDays > 7)
        {
            Add(AreaLiveTv, Sev.Tip, "A long TV guide is downloaded", $"{guideDays} days", "3–4 days",
                "The guide is downloaded for every channel; with a big IPTV list, extra days make Refresh Guide much slower and the database bigger.",
                "Dashboard → Live TV → Guide data");
        }
    }

    /// <summary>
    /// Live TV channels left behind after a source or categories were removed. Jellyfin doesn't always
    /// clear these itself. Read-only: Medic never edits the database; it points at the safe fix.
    /// </summary>
    private void CheckOrphanChannels()
    {
        int channels;
        try
        {
            var query = new InternalItemsQuery { Recursive = true };
            var prop = typeof(InternalItemsQuery).GetProperty("IncludeItemTypes");
            var element = prop?.PropertyType.GetElementType();
            if (prop is null || element is null || !element.IsEnum || !Enum.IsDefined(element, "LiveTvChannel"))
            {
                return;
            }

            var kinds = Array.CreateInstance(element, 1);
            kinds.SetValue(Enum.Parse(element, "LiveTvChannel"), 0);
            prop.SetValue(query, kinds);
            channels = _library.GetCount(query);
        }
        catch
        {
            return;
        }

        // Only worth raising on a big list, where deselected categories leave thousands behind.
        if (channels >= 2000)
        {
            Add(AreaLiveTv, Sev.Tip, "A very large number of Live TV channels", $"{channels:N0} channels",
                "If you've deselected categories, clear the leftovers the safe way (below)",
                "After you deselect IPTV categories, Jellyfin can leave the old channels behind in its database. The supported ways to clear them are: let Xtream Library's own cleanup remove them on its next sync (it has a 'clean up orphans' option), or remove and re-add the Live TV source in Dashboard → Live TV, which rebuilds the channel list. Medic can clear leftover stream files on disk (see the Dashboard maintenance panel), but it never edits Jellyfin's database — a hand-written database delete risks corrupting the whole server.",
                "Dashboard → Live TV, and Xtream Library settings");
        }
    }

    // ---------- Network ----------

    private void CheckNetwork(object? network)
    {
        if (network is null)
        {
            return;
        }

        bool remote = SettingsReader.Bool(network, "EnableRemoteAccess") ?? false;
        bool https = (SettingsReader.Bool(network, "EnableHttps") ?? false) || (SettingsReader.Bool(network, "RequireHttps") ?? false);
        var proxies = SettingsReader.List(network, "KnownProxies") ?? new List<string>();

        if (remote && !https && proxies.Count == 0)
        {
            Add(AreaNetwork, Sev.Tip, "Remote access is on without HTTPS", "Remote access on, no HTTPS or known proxy",
                "If you watch away from home, use a reverse proxy with HTTPS (e.g. Nginx Proxy Manager or SWAG) and add it under Known proxies",
                "Without HTTPS, passwords and viewing activity travel unencrypted over the internet. Ignore this if Jellyfin is only reachable at home or over a VPN.",
                "Dashboard → Networking");
        }
    }

    // ---------- Scheduled tasks ----------

    private void CheckTasks(UsageSummary usage)
    {
        var workers = _tasks.ScheduledTasks.ToList();

        var scan = workers.FirstOrDefault(t => t.Name.Contains("Scan Media Library", StringComparison.OrdinalIgnoreCase));
        var fastScan = scan?.Triggers?
            .Where(t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks.HasValue)
            .Select(t => TimeSpan.FromTicks(t.IntervalTicks.GetValueOrDefault()))
            .Where(i => i > TimeSpan.Zero && i < TimeSpan.FromHours(6))
            .OrderBy(i => i)
            .FirstOrDefault();
        if (fastScan is { } interval && interval > TimeSpan.Zero)
        {
            Add(AreaTasks, Sev.Improve, "The full library scan runs very often", $"Every {interval.TotalHours:0.#} hours",
                "Once a night, with Sonarr/Radarr notifications for new downloads",
                "Each full scan walks every folder. On a big library this keeps disks busy and slows the server for much of the day.",
                WhereTasks);
        }

        var optimise = workers.FirstOrDefault(t => t.Name.Contains("Optimize", StringComparison.OrdinalIgnoreCase));
        if (optimise is not null)
        {
            if (optimise.Triggers is null || !optimise.Triggers.Any())
            {
                Add(AreaTasks, Sev.Improve, "Database optimisation never runs", "Manual only", "Weekly, overnight",
                    "Without it the database slowly fragments, and the web UI and searches get slower.", WhereTasks);
            }
            else if (optimise.LastExecutionResult is { } last && last.EndTimeUtc < DateTime.UtcNow.AddDays(-14))
            {
                Add(AreaTasks, Sev.Tip, "Database optimisation hasn't run for a while",
                    $"Last ran {last.EndTimeUtc.ToLocalTime():d MMM}", "Weekly",
                    "It has a schedule but hasn't completed in over two weeks; check its schedule and errors.", WhereTasks);
            }
        }

        var failed = workers
            .Where(t => IsVisibleTask(t) && t.LastExecutionResult?.Status == TaskCompletionStatus.Failed)
            .Take(10)
            .ToList();
        foreach (var task in failed)
        {
            string error = (task.LastExecutionResult?.ErrorMessage ?? string.Empty).Trim().TrimEnd('.');
            string? owner = PluginNameFor(task);
            bool incompatible = error.Contains("Method not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("MissingMethod", StringComparison.OrdinalIgnoreCase)
                || error.Contains("TypeLoad", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Could not load type", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase);

            if (incompatible)
            {
                string who = owner is null ? "The plugin that provides it" : $"The {owner} plugin";
                Add(AreaTasks, Sev.Problem, $"\"{task.Name}\" can't work on this Jellyfin version",
                    "Fails every time it runs",
                    owner is null ? "Update the plugin it belongs to, or remove that plugin until an update is out"
                                  : $"Update {owner}, or remove it until a version for your Jellyfin is out",
                    $"{who} was built for an older Jellyfin and calls something that no longer exists, so this task can never succeed. Error: {Shorten(error)}.",
                    "Dashboard → Plugins");
            }
            else
            {
                Add(AreaTasks, Sev.Improve, $"\"{task.Name}\" failed last time it ran" + (owner is null ? string.Empty : $" ({owner} plugin)"),
                    error.Length == 0 ? "Failed, no error message" : Shorten(error),
                    "Check the full error in Medic's History tab",
                    "One-off failures are often temporary (a file in use, a network drop). If it keeps failing, the error shows the cause.",
                    WhereTasks);
            }
        }

        // Without a viewing pattern yet, fall back to a gentle daytime heuristic.
        if (!usage.Ready)
        {
            CheckDaytimeTasks(workers);
        }

        bool taskGrid = _plugins.Plugins.Any(p => string.Equals(SettingsReader.Text(p, "Name"), "Task Grid", StringComparison.OrdinalIgnoreCase));
        if (taskGrid)
        {
            Add(AreaTasks, Sev.Tip, "Task Grid and Medic can both change schedules", "Both installed",
                "Change schedules in one of them only",
                "A schedule edited in Task Grid is replaced the next time you press Apply recommended in Medic, and the other way round.",
                "Dashboard → Plugins");
        }

        if (!usage.Ready)
        {
            return;
        }

        var clashes = new List<string>();
        foreach (var worker in workers.Where(IsVisibleTask))
        {
            foreach (var trigger in worker.Triggers ?? Array.Empty<TaskTriggerInfo>())
            {
                if (!trigger.TimeOfDayTicks.HasValue)
                {
                    continue;
                }

                var at = TimeSpan.FromTicks(trigger.TimeOfDayTicks.GetValueOrDefault());
                int hour = at.Hours;
                IEnumerable<int> days = trigger.Type == TaskTriggerInfoType.WeeklyTrigger && trigger.DayOfWeek.HasValue
                    ? new[] { ((int)trigger.DayOfWeek.GetValueOrDefault() + 6) % 7 }
                    : Enumerable.Range(0, 7);

                if (days.Any(d => usage.AverageStreams.ElementAtOrDefault(d * 24 + hour) >= 1.0))
                {
                    clashes.Add($"{worker.Name} ({ScheduleStorage.Hhmm(at)})");
                    break;
                }
            }
        }

        if (clashes.Count > 0)
        {
            Add(AreaTasks, Sev.Improve, "Tasks run while people are usually watching", string.Join(", ", clashes),
                $"Move them into your quiet hours ({usage.QuietWindow})",
                "Heavy maintenance during viewing competes for disk and CPU and can cause buffering.",
                "Medic → Schedule → Apply recommended");
        }
    }

    /// <summary>
    /// In "suggest" mode, flags when Medic could schedule tasks noticeably better than they run now
    /// (for example tasks sitting in busy hours). It never changes anything itself; it points you at
    /// Preview → Apply. "Off" mode skips this entirely.
    /// </summary>
    private void SuggestSchedule(UsageSummary usage)
    {
        if ((Plugin.Instance?.Configuration.ScheduleMode ?? "suggest") == "off")
        {
            return;
        }

        List<PlannedTask> plan;
        try
        {
            var busy = BusyProfile.Create(usage, Plugin.Instance?.Configuration);
            plan = SchedulePlanner.Build(_tasks.ScheduledTasks.Where(IsVisibleTask).ToList(),
                ScheduleStorage.LoadProfile(_paths), ScheduleStorage.LoadManaged(_paths), busy, ScheduleStorage.LoadChoices(_paths));
        }
        catch
        {
            return;
        }

        var changing = plan.Where(p => p.Changes).ToList();
        // Only raise it when the change is worth making: a task currently in a busy hour, or several moves.
        bool worthwhile = changing.Count >= 3 || changing.Any(p => !string.IsNullOrEmpty(p.Warning));
        if (changing.Count > 0 && worthwhile)
        {
            Add(AreaTasks, Sev.Improve, "A better schedule is available",
                $"{changing.Count} task(s) could move to quieter times",
                "Open Schedule → Preview changes, then Apply if you're happy",
                usage.Ready
                    ? "Based on when people actually watch, Medic can space these tasks into quieter hours so they don't compete with playback. It won't change anything until you Apply."
                    : "Medic is still learning when people watch, but it can already tidy how these tasks are spread. It won't change anything until you Apply.",
                "Medic → Schedule");
        }
    }

    // ---------- Usage ----------

    private void CheckUsage(UsageSummary usage, object? enc, HardwareInfo hw)
    {
        if (!usage.Ready)
        {
            Add(AreaUsage, Sev.Tip, "Still learning when your server is busy",
                $"{usage.HoursCollected:0} hours recorded so far", "Leave it running for at least 3 days (a week is better)",
                "Medic checks who's watching every 5 minutes. Once it has enough, it can spot tasks running at busy times and suggest your best quiet hours.",
                "Nothing to do; it records automatically");
            return;
        }

        if (usage.MaxStreams == 0)
        {
            Add(AreaUsage, Sev.Good, "No playback recorded yet", "0 streams", string.Empty,
                "Nobody has watched anything since recording started, so there's no peak time to plan around.", string.Empty);
            return;
        }

        Add(AreaUsage, Sev.Good, "Your viewing pattern",
            $"Busiest: {usage.BusiestHours} (most on {usage.BusiestDay}); up to {usage.MaxStreams} at once",
            $"Quietest 5 hours: {usage.QuietWindow}",
            "Heavy tasks should run in the quiet hours. The Scheduled tasks section above flags any that don't.",
            "Medic → Schedule (shading shows busy hours)");


        string hwType = (SettingsReader.Text(enc, "HardwareAccelerationType") ?? "none").ToLowerInvariant();
        bool hwOn = hwType is not ("" or "none");
        if (usage.TranscodeShare is { } share && share >= 0.3)
        {
            Add(AreaUsage, hwOn ? Sev.Tip : Sev.Problem, $"{share:P0} of playback is transcoded",
                hwOn ? "Hardware acceleration on" : "CPU only",
                hwOn ? "Check app quality settings and the remote bitrate limit" : "Turn on hardware acceleration (see Hardware & transcoding)",
                hwOn ? "Lots of transcoding usually comes from apps set to a lower quality, subtitles that need burning in, or a low bitrate cap."
                     : $"Transcoding this much on {hw.CpuThreads} CPU threads is the most likely cause of buffering at busy times.",
                WhereTranscoding);
        }
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

    private string TranscodePath(object? enc)
    {
        string? path = SettingsReader.Text(enc, "TranscodingTempPath");
        return string.IsNullOrWhiteSpace(path) ? Path.Combine(_paths.CachePath, "transcodes") : path;
    }

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

    private static string Shorten(string text) => text.Length <= 600 ? text : text[..600] + "…";

    /// <summary>Heavy maintenance scheduled during the day, when people are likely to be watching.</summary>
    private void CheckDaytimeTasks(List<IScheduledTaskWorker> workers)
    {
        string[] heavy =
        {
            "Scan Media Library", "Optimize", "Optimise", "Chapter", "Trickplay", "Media Segment", "Keyframe",
            "Detect and Analyze", "Refresh People", "Audio Normali", "Subtitle", "Refresh Guide"
        };

        var daytime = new List<string>();
        foreach (var worker in workers.Where(IsVisibleTask))
        {
            if (!heavy.Any(h => worker.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
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
                "Medic → Schedule");
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


    private static bool IsVisibleTask(IScheduledTaskWorker worker) =>
        worker.ScheduledTask is not IConfigurableScheduledTask configurable ||
        (!configurable.IsHidden && configurable.IsEnabled);

    private static bool SameDrive(string a, string b) =>
        SystemProbe.Mount(a)?.MountPoint == SystemProbe.Mount(b)?.MountPoint;

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

    private void Add(string area, string severity, string title, string current, string recommended, string why, string where) =>
        _report.Findings.Add(new Finding
        {
            Area = area,
            Severity = severity,
            Title = title,
            Current = current,
            Recommended = recommended,
            Why = why,
            Where = where
        });

    private void Guard(string area, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex)
        {
            Add(area, Sev.Tip, "Some checks in this area couldn't run", ex.GetType().Name, string.Empty,
                $"Medic hit an error reading these settings ({ex.Message}). The rest of the report is unaffected.", string.Empty);
        }
    }
}

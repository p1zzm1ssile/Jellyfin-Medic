using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using JellyfinMedic.Services;

namespace JellyfinMedic.Api;

[ApiController]
[Route("JellyfinMedic")]
[Authorize(Policy = Policies.RequiresElevation)] // Admins only: shows server paths and settings, and runs tests.
public class MedicController : ControllerBase
{
    // Only one test at a time, so two tests can't compete for the GPU or the connection.
    private static readonly SemaphoreSlim TestLock = new(1, 1);

    private readonly IServerConfigurationManager _config;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly IPluginManager _pluginManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IUserManager _users;
    private readonly ISessionManager _sessions;
    private readonly IInstallationManager _installs;
    private readonly object? _activity;
    private readonly ITaskManager _tasks;
    private readonly IServerApplicationHost _host;
    private readonly DiagnosticsEngine _engine;

    public MedicController(
        IServerConfigurationManager config,
        IApplicationPaths paths,
        ILibraryManager library,
        ITaskManager tasks,
        IPluginManager pluginManager,
        IServerApplicationHost host,
        IMediaEncoder mediaEncoder,
        IUserManager users,
        ISessionManager sessions,
        IInstallationManager installs,
        IServiceProvider services)
    {
        _users = users;
        _activity = ResolveActivityManager(services);
        _sessions = sessions;
        _installs = installs;
        _tasks = tasks;
        _host = host;
        _config = config;
        _paths = paths;
        _library = library;
        _pluginManager = pluginManager;
        _mediaEncoder = mediaEncoder;
        _engine = new DiagnosticsEngine(config, paths, library, tasks, pluginManager, host);
    }

    /// <summary>Full check: specs plus every finding, most serious first.</summary>
    [HttpGet("Report")]
    public async Task<ActionResult<DiagnosticReport>> GetReport(CancellationToken cancellationToken)
    {
        var usage = UsageAnalyzer.Summarise(UsageStore.Load(_paths));
        var plugins = PluginAuditor.Audit(_pluginManager, _paths, _engine.CreatePluginContext());
        var links = _engine.LinksToCheck();
        var linkResults = await LinkChecker.CheckAsync(links.Select(l => l.Url), cancellationToken).ConfigureAwait(false);

        var settings = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var (userSummary, userFindings) = UserAuditor.Audit(_users, settings.InactiveUserDays);
        var repeated = LogScanner.Scan(_paths.LogDirectoryPath);
        var updates = await UpdateChecker.AvailableAsync(_installs, cancellationToken).ConfigureAwait(false);
        var userList = UserList.All(_users);

        var extra = new List<Finding>();
        extra.AddRange(userFindings);
        extra.AddRange(SecurityAuditor.Audit(_activity, _users, userList));
        extra.AddRange(LogScanner.Findings(repeated));
        extra.AddRange(IptvAnalyzer.Findings());
        if (updates.Count > 0)
        {
            extra.Add(new Finding
            {
                Area = "Plugins",
                Severity = Sev.Tip,
                Title = updates.Count == 1 ? "A plugin update is available" : $"{updates.Count} plugin updates are available",
                Current = string.Join(", ", updates),
                Recommended = "Install them, then restart Jellyfin",
                Why = "Updates often fix bugs and keep plugins working with new Jellyfin versions.",
                Where = "Dashboard → Plugins, or run the Update Plugins task"
            });
        }

        var report = _engine.Run(usage, plugins, links, linkResults, extra);
        report.Users = userSummary;
        report.Updates = updates;
        report.RepeatedErrors = repeated;
        return Ok(report);
    }

    /// <summary>Every core and library setting, with passwords and keys hidden.</summary>
    [HttpGet("JellyfinSettings")]
    public ActionResult<List<SettingRow>> GetSettings() => Ok(_engine.AllSettings());

    // ---------- Dashboard, IPTV, export and Medic's own settings ----------

    /// <summary>What Jellyfin is doing right now. Cheap enough to refresh every few seconds.</summary>
    [HttpGet("Now")]
    public ActionResult<NowSnapshot> GetNow() => Ok(ServerNow.Snapshot(_sessions, _tasks));

    /// <summary>Recent times the load guard stopped or restarted a task under memory pressure.</summary>
    [HttpGet("LoadGuard")]
    public ActionResult<List<LoadGuardEvent>> GetLoadGuard() => Ok(LoadGuardLog.Load(_paths));

    // ---------- Media report (read-only) ----------

    /// <summary>The last media report, build progress, and the transcode log.</summary>
    [HttpGet("MediaReport")]
    public ActionResult<MediaReportStatus> GetMediaReport() => Ok(MediaReport.Status(_paths));

    /// <summary>Starts building a fresh media report in the background.</summary>
    [HttpPost("MediaReport/Build")]
    public ActionResult<MediaReportStatus> BuildMediaReport()
    {
        string hw = SettingsReader.Text(_config.GetConfiguration("encoding"), "HardwareAccelerationType") ?? "none";
        MediaReport.Start(_library, _engine.Libraries, _paths, hw);
        return Ok(MediaReport.Status(_paths));
    }

    // ---------- Track cleaner ----------

    /// <summary>Scans local files and previews which audio/subtitle tracks would be removed.</summary>
    [HttpGet("Tracks/Scan")]
    public ActionResult<TrackScanResult> TracksScan()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(TrackCleaner.Scan(_library, _engine.Libraries, cfg));
    }

    /// <summary>Starts a scan in the background; poll Tracks/Scan/Status for progress and the result.</summary>
    [HttpPost("Tracks/Scan/Start")]
    public ActionResult<TrackScanStatus> TracksScanStart()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        TrackCleaner.StartScan(_library, _engine.Libraries, cfg);
        return Ok(TrackCleaner.ScanStatus());
    }

    /// <summary>The background scan's progress, and the last finished scan.</summary>
    [HttpGet("Tracks/Scan/Status")]
    public ActionResult<TrackScanStatus> TracksScanStatus() => Ok(TrackCleaner.ScanStatus());

    /// <summary>Starts a run. dryRun=true (default) only reports; dryRun=false actually remuxes.</summary>
    [HttpPost("Tracks/Run")]
    public ActionResult<object> TracksRun([FromQuery] bool dryRun = true)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        string? ffmpeg = FfmpegPath();
        if (!dryRun && ffmpeg is null)
        {
            return Problem("FFmpeg couldn't be found, so tracks can't be stripped.");
        }

        if (TrackCleaner.CurrentProgress().Running)
        {
            return StatusCode(StatusCodes.Status409Conflict, "A track run is already going.");
        }

        if (!dryRun)
        {
            TrackCleaner.ClearScan(); // files are about to change, so the last scan goes out of date
        }

        _ = TrackCleaner.RunAsync(_library, _engine.Libraries, ffmpeg ?? string.Empty, cfg, dryRun);
        return Ok(new { Started = true, DryRun = dryRun });
    }

    [HttpGet("Tracks/Progress")]
    public ActionResult<TrackRunProgress> TracksProgress() => Ok(TrackCleaner.CurrentProgress());

    [HttpPost("Tracks/Stop")]
    public ActionResult<object> TracksStop()
    {
        TrackCleaner.Stop();
        return Ok(new { Stopped = true });
    }

    /// <summary>Puts titles left with several copies by earlier versions back to a single file.</summary>
    [HttpPost("Tracks/TidyCopies")]
    public ActionResult<object> TracksTidyCopies([FromQuery] bool deleteOriginals = false)
    {
        if (TrackCleaner.CurrentProgress().Running)
        {
            return StatusCode(StatusCodes.Status409Conflict, "Wait for the track run to finish first.");
        }

        var (titles, copies, freed) = TrackCleaner.TidyEarlierCopies(_library, _engine.Libraries, deleteOriginals);
        TrackCleaner.ClearScan();
        return Ok(new { Titles = titles, CopiesDeleted = copies, Freed = freed, FreedText = SystemProbe.Size(freed) });
    }

    /// <summary>How many originals Medic is keeping, and how much space they take.</summary>
    [HttpGet("Tracks/KeptOriginals")]
    public ActionResult<object> TracksKeptOriginals()
    {
        var (files, bytes) = TrackCleaner.KeptOriginals(_library, _engine.Libraries);
        return Ok(new { Files = files, Bytes = bytes, SizeText = SystemProbe.Size(bytes) });
    }

    /// <summary>Deletes the originals Medic kept in .medic-originals folders (only where the cleaned file is in place).</summary>
    [HttpPost("Tracks/DeleteOriginals")]
    public ActionResult<object> TracksDeleteOriginals()
    {
        var (deleted, freed) = TrackCleaner.DeleteOriginals(_library, _engine.Libraries);
        return Ok(new { Deleted = deleted, Freed = freed, FreedText = SystemProbe.Size(freed) });
    }

    /// <summary>The community plugin list from awesome-jellyfin (fetched live, credited to them).</summary>
    [HttpGet("Directory")]
    public async Task<ActionResult<PluginDirectory>> GetDirectory(CancellationToken cancellationToken) =>
        Ok(await PluginDirectoryService.GetAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Plugins worth adding, considering or removing, from this server's libraries, viewing and installed plugins.</summary>
    [HttpGet("PluginSuggestions")]
    public async Task<ActionResult<PluginAdvice>> GetPluginSuggestions(CancellationToken cancellationToken) =>
        Ok(await PluginAdvisor.BuildAsync(_library, _users, _pluginManager, _installs, cancellationToken).ConfigureAwait(false));

    /// <summary>Medic's current version and its changelog, for the "what's new" panel after an update.</summary>
    [HttpGet("Version")]
    public ActionResult<object> GetVersion()
    {
        string version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return Ok(new { Version = version, Changelog = ChangelogReader.Read() });
    }

    /// <summary>The last IPTV analysis, if one has been run since Jellyfin started.</summary>
    [HttpGet("Iptv")]
    public ActionResult<IptvReport?> GetIptv() => Ok(IptvAnalyzer.Last);

    /// <summary>Runs the IPTV analysis. Reads a lot, so it only runs when asked.</summary>
    [HttpPost("Iptv")]
    public async Task<ActionResult<IptvReport>> RunIptv(CancellationToken cancellationToken)
    {
        if (!await TestLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status409Conflict, "A test or analysis is already running.");
        }

        try
        {
            return Ok(IptvAnalyzer.Analyse(_library, _users, _engine.Libraries));
        }
        finally
        {
            TestLock.Release();
        }
    }

    /// <summary>Every plugin's settings with secrets masked, as text, for asking for help.</summary>
    [HttpGet("Export")]
    public ActionResult GetExport([FromQuery] bool includeSystem = false)
    {
        string version = SettingsReader.Text(_host, "ApplicationVersionString") ?? SettingsReader.Text(_host, "ApplicationVersion") ?? "unknown";
        var summary = includeSystem ? SystemSummary(version) : null;
        string text = SettingsExporter.Build(_pluginManager, _paths, version, summary);
        return Content(text, "text/plain", Encoding.UTF8);
    }

    /// <summary>
    /// A short, anonymous picture of the server for tuning advice: hardware make and model (no
    /// serial numbers), memory, OS, storage, library sizes and installed plugins. No names, paths,
    /// addresses or accounts.
    /// </summary>
    private List<string> SystemSummary(string version)
    {
        var hw = SystemProbe.Probe();
        var lines = new List<string>
        {
            "Jellyfin version: " + version,
            "Operating system: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            "Running on: " + HostPlatform.Label,
            "CPU: " + (hw.CpuModel ?? "unknown") + " (" + hw.CpuThreads + " threads)",
            "GPU: " + hw.GpuDescription,
            "Memory available to Jellyfin: " + SystemProbe.Gb(hw.MemoryGb),
        };

        if (hw.HostMemoryGb is { } host)
        {
            lines.Add("Memory in the machine: " + SystemProbe.Gb(host));
        }

        foreach (var (label, path) in new[] { ("Config/database drive", _paths.DataPath), ("Cache drive", _paths.CachePath) })
        {
            if (SystemProbe.Space(path) is { } s)
            {
                string fs = SystemProbe.Mount(path)?.FsType ?? "unknown";
                lines.Add($"{label}: {SystemProbe.Gb(s.FreeGb)} free of {SystemProbe.Gb(s.TotalGb)} ({fs})");
            }
        }

        long db = SystemProbe.FileSize(System.IO.Path.Combine(_paths.DataPath, "jellyfin.db"));
        if (db > 0)
        {
            lines.Add("Database size: " + SystemProbe.Size(db));
        }

        try
        {
            foreach (var lib in _engine.Libraries)
            {
                lines.Add($"Library \"{lib.Name}\": {(lib.ItemCount?.ToString("N0") ?? "?")} items{(lib.IsStreamed ? " (IPTV/.strm)" : string.Empty)}");
            }
        }
        catch
        {
            // Library counts aren't essential to the summary.
        }

        foreach (var plugin in _pluginManager.Plugins.Cast<object>().OrderBy(p => SettingsReader.Text(p, "Name"), StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"Plugin: {SettingsReader.Text(plugin, "Name")} {SettingsReader.Text(plugin, "Version")} ({SettingsReader.Text(plugin, "Manifest.Status")})");
        }

        return lines;
    }

    [HttpGet("MedicSettings")]
    public ActionResult<MedicSettingsDto> GetMedicSettings()
    {
        var c = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(new MedicSettingsDto
        {
            AvoidEnabled = c.AvoidEnabled, AvoidStartHour = c.AvoidStartHour, AvoidEndHour = c.AvoidEndHour,
            InactiveUserDays = c.InactiveUserDays, LoadGuardEnabled = c.LoadGuardEnabled,
            MemoryCeilingPercent = c.MemoryCeilingPercent, ScheduleMode = c.ScheduleMode,
            TracksKeepLanguages = c.TracksKeepLanguages, TracksRemoveUndetermined = c.TracksRemoveUndetermined,
            TracksRemoveUntaggedSubtitles = c.TracksRemoveUntaggedSubtitles,
            TracksKeepFirstUntaggedSubtitle = c.TracksKeepFirstUntaggedSubtitle,
            TracksAllowRemovingOnlySubtitle = c.TracksAllowRemovingOnlySubtitle,
            TracksReplaceInPlace = c.TracksReplaceInPlace, TracksConcurrentFiles = c.TracksConcurrentFiles,
            TracksFfmpegThreads = c.TracksFfmpegThreads
        });
    }

    [HttpPost("MedicSettings")]
    public ActionResult<MedicSettingsDto> SaveMedicSettings([FromBody] MedicSettingsDto settings)
    {
        if (Plugin.Instance is null)
        {
            return Problem("Medic isn't fully loaded yet. Try again in a moment.");
        }

        var c = Plugin.Instance.Configuration;
        c.AvoidEnabled = settings.AvoidEnabled;
        c.AvoidStartHour = Math.Clamp(settings.AvoidStartHour, 0, 23);
        c.AvoidEndHour = Math.Clamp(settings.AvoidEndHour, 0, 24);
        c.InactiveUserDays = Math.Clamp(settings.InactiveUserDays, 7, 3650);
        c.LoadGuardEnabled = settings.LoadGuardEnabled;
        c.MemoryCeilingPercent = Math.Clamp(settings.MemoryCeilingPercent, 60, 95);
        c.ScheduleMode = settings.ScheduleMode == "off" ? "off" : "suggest";
        c.TracksKeepLanguages = string.IsNullOrWhiteSpace(settings.TracksKeepLanguages) ? "eng" : settings.TracksKeepLanguages.Trim();
        c.TracksRemoveUndetermined = settings.TracksRemoveUndetermined;
        c.TracksRemoveUntaggedSubtitles = settings.TracksRemoveUntaggedSubtitles;
        c.TracksKeepFirstUntaggedSubtitle = settings.TracksKeepFirstUntaggedSubtitle;
        c.TracksAllowRemovingOnlySubtitle = settings.TracksAllowRemovingOnlySubtitle;
        c.TracksReplaceInPlace = settings.TracksReplaceInPlace;
        c.TracksConcurrentFiles = Math.Clamp(settings.TracksConcurrentFiles, 1, 4);
        c.TracksFfmpegThreads = Math.Clamp(settings.TracksFfmpegThreads, 0, 16);
        Plugin.Instance.SaveConfiguration();
        TrackCleaner.ClearScan(); // the last track scan was made with the old settings
        return GetMedicSettings();
    }

    /// <summary>Every installed plugin with its status, settings (secrets hidden) and findings.</summary>
    [HttpGet("Plugins")]
    public ActionResult<List<PluginReport>> GetPlugins()
    {
        var reports = PluginAuditor.Audit(_pluginManager, _paths, _engine.CreatePluginContext());
        var ignored = IgnoreStore.Load(_paths);
        foreach (var report in reports)
        {
            IgnoreStore.Apply(report.Findings, ignored);
        }

        return Ok(reports);
    }

    /// <summary>Ignore a finding the admin has checked and is happy with, or show it again.</summary>
    [HttpPost("Ignore")]
    public ActionResult<object> SetIgnored([FromQuery] string key, [FromQuery] bool ignored = true)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 500)
        {
            return BadRequest(new { Success = false });
        }

        IgnoreStore.Set(_paths, key, ignored);
        return Ok(new { Success = true, Key = key, Ignored = ignored });
    }

    /// <summary>Viewing pattern recorded so far.</summary>
    [HttpGet("Usage")]
    public ActionResult<UsageSummary> GetUsage() => Ok(UsageAnalyzer.Summarise(UsageStore.Load(_paths)));

    // ---------- Tests ----------

    /// <summary>
    /// After the page has played a film through Jellyfin's transcoder, reads the matching FFmpeg
    /// log to see whether the GPU did the decoding, encoding and tone mapping.
    /// </summary>
    [HttpGet("TranscodeLog")]
    public ActionResult<TranscodeLogResult> GetTranscodeLog([FromQuery] DateTime sinceUtc, [FromQuery] string? match) =>
        Ok(TranscodeLogReader.Read(_paths.LogDirectoryPath, sinceUtc, match, ConfiguredAcceleration()));

    /// <summary>Times trickplay/chapter-image style scanning of one film, GPU against CPU.</summary>
    [HttpPost("DecodeTest")]
    public async Task<ActionResult<DecodeTestResult>> RunDecodeTest([FromQuery] Guid itemId, CancellationToken cancellationToken)
    {
        string? path = _library.GetItemById(itemId)?.Path;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return NotFound("That film's file couldn't be found on the server.");
        }

        string? ffmpeg = FfmpegPath();
        if (ffmpeg is null)
        {
            return Problem("FFmpeg couldn't be found, so the test can't run.");
        }

        if (!await TestLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status409Conflict, "Another test is already running.");
        }

        try
        {
            var encoding = _config.GetConfiguration("encoding");
            return Ok(await DecodeTest.RunAsync(
                ffmpeg,
                path,
                ConfiguredAcceleration(),
                SettingsReader.Text(encoding, "VaapiDevice"),
                cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            TestLock.Release();
        }
    }

    /// <summary>Last saved internet speed test, if any.</summary>
    [HttpGet("SpeedTest")]
    public ActionResult<SpeedTestResult?> GetSpeedTest()
    {
        var last = SpeedStore.Load(_paths);
        if (last is not null)
        {
            last.CurrentLimitBps = _config.Configuration.RemoteClientBitrateLimit;
        }

        return Ok(last);
    }

    /// <summary>Runs a new internet speed test from the server and saves the result.</summary>
    [HttpPost("SpeedTest")]
    public async Task<ActionResult<SpeedTestResult>> RunSpeedTest(CancellationToken cancellationToken)
    {
        if (!await TestLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status409Conflict, "Another test is already running.");
        }

        try
        {
            var result = await SpeedTester.RunAsync(_config.Configuration.RemoteClientBitrateLimit, cancellationToken).ConfigureAwait(false);
            if (result.Error is null)
            {
                SpeedStore.Save(_paths, result);
            }

            return Ok(result);
        }
        finally
        {
            TestLock.Release();
        }
    }

    private string ConfiguredAcceleration()
    {
        string hw = SettingsReader.Text(_config.GetConfiguration("encoding"), "HardwareAccelerationType") ?? "none";
        return string.IsNullOrWhiteSpace(hw) ? "none" : hw.Trim().ToLowerInvariant();
    }

    private string? FfmpegPath()
    {
        var encoding = _config.GetConfiguration("encoding");
        foreach (var candidate in new[]
                 {
                     SettingsReader.Text(_mediaEncoder, "EncoderPath"),
                     SettingsReader.Text(encoding, "EncoderAppPathDisplay"),
                     SettingsReader.Text(encoding, "EncoderAppPath"),
                     "/usr/lib/jellyfin-ffmpeg/ffmpeg"
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // ---------- Stop tasks, exposure, clean-up ----------

    /// <summary>Stops every scheduled task that's currently running.</summary>
    [HttpPost("StopRunningTasks")]
    public ActionResult<object> StopRunningTasks()
    {
        var running = _tasks.ScheduledTasks.Where(t => t.State == TaskState.Running).ToList();
        var stopped = new List<string>();
        foreach (var task in running)
        {
            try
            {
                _tasks.Cancel(task);
                stopped.Add(task.Name);
            }
            catch
            {
                // Already finishing; ignore.
            }
        }

        return Ok(new { Success = true, Stopped = stopped.Count, Names = stopped });
    }

    /// <summary>The address, port and protocol to offer in the exposure check (your public IP and domain, if known).</summary>
    [HttpGet("ExposureDefaults")]
    public async Task<ActionResult<ExposureDefaults>> GetExposureDefaults(CancellationToken cancellationToken) =>
        Ok(await ExposureChecker.DefaultsAsync(TryNetwork(), cancellationToken).ConfigureAwait(false));

    /// <summary>Checks whether Jellyfin answers from the internet at a host/port/protocol. Sends only that address.</summary>
    [HttpPost("ExposureCheck")]
    public async Task<ActionResult<ExposureResult>> ExposureCheck([FromQuery] string host, [FromQuery] int port = 443, [FromQuery] string protocol = "https", CancellationToken cancellationToken = default)
    {
        return Ok(await ExposureChecker.CheckAsync(host, port, protocol, cancellationToken).ConfigureAwait(false));
    }

    private object? TryNetwork()
    {
        try
        {
            return _config.GetConfiguration("network");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Disk space that can usually be freed safely.</summary>
    [HttpGet("Cleanup")]
    public ActionResult<CleanupReport> GetCleanup() => Ok(Housekeeping.Scan(_paths, TranscodePath()));

    /// <summary>Removes the leftover files of one kind, after the admin confirms on the page.</summary>
    [HttpPost("Cleanup")]
    public ActionResult<object> RunCleanup([FromQuery] string kind)
    {
        var (ok, message, freed) = Housekeeping.Clean(kind ?? string.Empty, _paths, TranscodePath());
        return Ok(new { Success = ok, Message = message, Freed = freed });
    }

    private string TranscodePath()
    {
        string? path = SettingsReader.Text(_config.GetConfiguration("encoding"), "TranscodingTempPath");
        return string.IsNullOrWhiteSpace(path) ? System.IO.Path.Combine(_paths.CachePath, "transcodes") : path;
    }

    private static object? ResolveActivityManager(IServiceProvider services)
    {
        foreach (var name in new[] { "MediaBrowser.Model.Activity.IActivityManager", "Jellyfin.Data.IActivityManager" })
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(name))
                .FirstOrDefault(t => t is not null);
            if (type is not null)
            {
                try
                {
                    var service = services.GetService(type);
                    if (service is not null)
                    {
                        return service;
                    }
                }
                catch
                {
                    // Not registered in this version; try the next name.
                }
            }
        }

        return null;
    }
}

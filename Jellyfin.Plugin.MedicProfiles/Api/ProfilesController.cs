using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Jellyfin.Plugin.MedicProfiles.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Api;

/// <summary>
/// Everything under /MedicProfiles is for admins only: it can remove downloads and change what
/// Sonarr and Radarr import. The Sonarr and Radarr keys never leave the server.
/// </summary>
[ApiController]
[Route("MedicProfiles")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class ProfilesController : ControllerBase
{
    private readonly ArrStore _store;
    private readonly ArrClient _arr;
    private readonly DownloadsService _downloads;
    private readonly ProfileAdvisor _advisor;
    private readonly IndexersService _indexers;
    private readonly QbitFileFilter _qbit;
    private readonly ILogger<ProfilesController> _logger;

    public ProfilesController(ArrStore store, ArrClient arr, DownloadsService downloads, ProfileAdvisor advisor, IndexersService indexers, QbitFileFilter qbit, ILogger<ProfilesController> logger)
    {
        _logger = logger;
        _indexers = indexers;
        _qbit = qbit;
        _store = store;
        _arr = arr;
        _downloads = downloads;
        _advisor = advisor;
    }

    // ---------- Settings ----------

    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        return Json(new
        {
            sonarr = new { url = config.SonarrUrl, keySet = _store.GetKey(ArrApp.Sonarr) is not null },
            radarr = new { url = config.RadarrUrl, keySet = _store.GetKey(ArrApp.Radarr) is not null },
            stuckAfterHours = config.StuckAfterHours,
            qbit = new { url = config.QbitUrl, user = config.QbitUser, passwordSet = _store.GetQbitPassword() is not null }
        });
    }

    [HttpPost("Settings")]
    public ActionResult SaveSettings([FromBody] SettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest();
        }

        var plugin = Plugin.Instance!;
        string sonarrUrl = CleanUrl(request.SonarrUrl), radarrUrl = CleanUrl(request.RadarrUrl);

        // A saved key or password is only ever sent to the server it was entered for: pointed at another
        // server, it has to be typed again, so a changed address can't be used to collect it.
        bool newSonarrHost = HostChanged(plugin.Configuration.SonarrUrl, sonarrUrl);
        bool newRadarrHost = HostChanged(plugin.Configuration.RadarrUrl, radarrUrl);
        bool newQbitHost = request.QbitUrl is not null && HostChanged(plugin.Configuration.QbitUrl, CleanUrl(request.QbitUrl));

        // Kept so a failed write, of a secret or of the addresses, can put back everything this request changed.
        var secretsBefore = (Sonarr: _store.GetKey(ArrApp.Sonarr), Radarr: _store.GetKey(ArrApp.Radarr), Qbit: _store.GetQbitPassword());
        var settingsBefore = SavedSettings.Of(plugin.Configuration);

        // The keys and password are written before the new addresses are saved: if a write fails, the ones already
        // written are put back and the settings stay as they were, so no secret is left paired with the wrong server.
        try
        {
            if (request.ClearQbitPassword || (newQbitHost && string.IsNullOrEmpty(request.QbitPassword)))
            {
                _store.SetQbitPassword(null);
            }
            else if (!string.IsNullOrEmpty(request.QbitPassword))
            {
                _store.SetQbitPassword(request.QbitPassword);
            }

            // A blank key box means "keep the saved key" (for the same server); Remove clears it.
            if (request.ClearSonarrKey || (newSonarrHost && string.IsNullOrWhiteSpace(request.SonarrKey)))
            {
                _store.SetKey(ArrApp.Sonarr, null);
            }
            else if (!string.IsNullOrWhiteSpace(request.SonarrKey))
            {
                _store.SetKey(ArrApp.Sonarr, request.SonarrKey);
            }

            if (request.ClearRadarrKey || (newRadarrHost && string.IsNullOrWhiteSpace(request.RadarrKey)))
            {
                _store.SetKey(ArrApp.Radarr, null);
            }
            else if (!string.IsNullOrWhiteSpace(request.RadarrKey))
            {
                _store.SetKey(ArrApp.Radarr, request.RadarrKey);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(ex, "Medic Profiles: couldn't save a key or password, so the settings weren't changed");
            return PutBackSecrets(secretsBefore, newSonarrHost, newRadarrHost, newQbitHost)
                ? Problem("Couldn't save the key or password, so nothing was changed. Check the Jellyfin log for Medic Profiles errors.")
                : NotPutBack();
        }

        plugin.Configuration.SonarrUrl = sonarrUrl;
        plugin.Configuration.RadarrUrl = radarrUrl;
        plugin.Configuration.StuckAfterHours = Math.Clamp(request.StuckAfterHours ?? plugin.Configuration.StuckAfterHours, 1, 168);
        if (request.QbitUrl is not null)
        {
            plugin.Configuration.QbitUrl = CleanUrl(request.QbitUrl);
            plugin.Configuration.QbitUser = (request.QbitUser ?? string.Empty).Trim();
        }

        if (newQbitHost)
        {
            // What Medic added to the old qBittorrent's exclusions says nothing about this one: Stop blocking there
            // must not take out the owner's own entries.
            plugin.Configuration.QbitPatternsAdded = Array.Empty<string>();
            plugin.Configuration.QbitListWasOn = null;
            plugin.Configuration.QbitPatternsHost = Authority(plugin.Configuration.QbitUrl);
        }

        try
        {
            plugin.SaveConfiguration();
        }
        catch (Exception ex)
        {
            // Whatever stopped the save, a key written above must not stay paired with the old address.
            _logger.LogWarning(ex, "Medic Profiles: couldn't save the settings, so the keys and password are put back as they were");
            settingsBefore.PutBack(plugin.Configuration);
            return PutBackSecrets(secretsBefore, newSonarrHost, newRadarrHost, newQbitHost)
                ? Problem("Couldn't save the settings, so nothing was changed. Check the Jellyfin log for Medic Profiles errors.")
                : NotPutBack();
        }

        _advisor.Forget();

        return NoContent();
    }

    [HttpPost("Test/{app}")]
    public async Task<ActionResult> Test([FromRoute] string app, CancellationToken ct)
    {
        if (ParseApp(app) is not { } a)
        {
            return BadRequest();
        }

        var (ok, message) = await _arr.StatusAsync(a, ct).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    // ---------- Downloads ----------

    [HttpGet("Queue")]
    public async Task<ActionResult> GetQueue(CancellationToken ct)
    {
        var (rows, errors, notes) = await _downloads.QueueAsync(ct).ConfigureAwait(false);
        return Json(new { configured = Configured(), rows, errors, notes });
    }

    [HttpPost("Queue/Remove")]
    public async Task<ActionResult> Remove([FromBody] RemoveRequest request, CancellationToken ct)
    {
        if (request is null || ParseApp(request.App) is not { } app)
        {
            return BadRequest();
        }

        var (ok, message) = await _downloads.RemoveAsync(app, request.QueueIds ?? new List<int>(), request.Block, request.SearchAgain, request.KeepInClient, request.Title ?? "A download", UserName(), ct).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    [HttpGet("Indexers")]
    public async Task<ActionResult> GetIndexers(CancellationToken ct)
    {
        var (rows, warnings, errors) = await _indexers.StatusAsync(ct).ConfigureAwait(false);
        return Json(new { configured = Configured(), rows, warnings, errors });
    }

    // ---------- Unsafe files in qBittorrent ----------

    [HttpGet("FileFilter")]
    public async Task<ActionResult> GetFileFilter(CancellationToken ct) => Json(await _qbit.StateAsync(ct).ConfigureAwait(false));

    // Every button on the page saves the settings and reloads this state, so a refused sign-in is only tried
    // again here, when the owner asks: each failure counts towards qBittorrent's ban on this server.
    [HttpPost("FileFilter/Retry")]
    public async Task<ActionResult> RetryFileFilter(CancellationToken ct) => Json(await _qbit.RetryAsync(ct).ConfigureAwait(false));

    [HttpPost("FileFilter")]
    public async Task<ActionResult> SetFileFilter([FromBody] FileFilterRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest();
        }

        var (ok, message, refused) = await _qbit.ApplyAsync(request.Block, request.Archives, ct).ConfigureAwait(false);
        if (ok)
        {
            _store.Record(new ActionRecord
            {
                Utc = DateTime.UtcNow,
                App = "qbittorrent",
                Action = request.Block ? "blocked files" : "unblocked files",
                Title = request.Block ? (request.Archives ? "Program files and archives" : "Program files") : "Program files and archives",
                Detail = "qBittorrent's excluded file names",
                By = UserName()
            });
        }

        return Json(new { ok, message, refused });
    }

    public class FileFilterRequest
    {
        public bool Block { get; set; }

        public bool Archives { get; set; }
    }

    [HttpGet("Blocked")]
    public async Task<ActionResult> GetBlocked(CancellationToken ct)
    {
        var (rows, errors, notes) = await _downloads.BlockedAsync(ct).ConfigureAwait(false);
        return Json(new { configured = Configured(), rows, errors, notes });
    }

    [HttpPost("Blocked/Unblock")]
    public async Task<ActionResult> Unblock([FromBody] UnblockRequest request, CancellationToken ct)
    {
        if (request is null || ParseApp(request.App) is not { } app || request.Id <= 0)
        {
            return BadRequest();
        }

        var (ok, message) = await _downloads.UnblockAsync(app, request.Id, request.Title ?? "A release", UserName(), ct).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    [HttpGet("Import/{app}")]
    public async Task<ActionResult> GetImportFiles([FromRoute] string app, [FromQuery] string downloadId, CancellationToken ct)
    {
        if (ParseApp(app) is not { } a || string.IsNullOrWhiteSpace(downloadId))
        {
            return BadRequest();
        }

        var (files, error) = await _downloads.ImportFilesAsync(a, downloadId, ct).ConfigureAwait(false);
        return Json(new { ok = files is not null, message = error, files });
    }

    [HttpGet("Library/{app}")]
    public async Task<ActionResult> SearchLibrary([FromRoute] string app, [FromQuery] string? q, CancellationToken ct)
    {
        if (ParseApp(app) is not { } a)
        {
            return BadRequest();
        }

        var (results, error) = await _downloads.SearchLibraryAsync(a, q ?? string.Empty, ct).ConfigureAwait(false);
        return Json(new { ok = results is not null, message = error, results });
    }

    [HttpGet("Episodes")]
    public async Task<ActionResult> GetEpisodes([FromQuery] int seriesId, CancellationToken ct)
    {
        if (seriesId <= 0)
        {
            return BadRequest();
        }

        var (episodes, error) = await _downloads.EpisodesAsync(seriesId, ct).ConfigureAwait(false);
        return Json(new { ok = episodes is not null, message = error, episodes });
    }

    [HttpPost("Import")]
    public async Task<ActionResult> Import([FromBody] ImportRequest request, CancellationToken ct)
    {
        if (request is null || ParseApp(request.App) is not { } app || string.IsNullOrWhiteSpace(request.DownloadId))
        {
            return BadRequest();
        }

        var (ok, message) = await _downloads.ImportAsync(app, request.DownloadId, request.ImportMode ?? "auto", request.Files ?? new List<ImportFileRequest>(), request.Title ?? "A download", UserName(), ct).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    [HttpGet("History")]
    public ActionResult GetHistory() => Json(new { rows = _store.LoadHistory().Take(50) });

    // ---------- Profiles ----------

    [HttpGet("Advice")]
    public async Task<ActionResult> GetAdvice([FromQuery] bool refresh, CancellationToken ct)
    {
        var report = await _advisor.GetAsync(refresh, ct).ConfigureAwait(false);
        return Json(new { configured = Configured(), report });
    }

    // ---------- Helpers ----------

    private object Configured() => new { sonarr = _arr.IsConfigured(ArrApp.Sonarr), radarr = _arr.IsConfigured(ArrApp.Radarr) };

    private string? UserName() => User.Identity?.Name;

    private static ArrApp? ParseApp(string? app) => app?.ToLowerInvariant() switch
    {
        "sonarr" => ArrApp.Sonarr,
        "radarr" => ArrApp.Radarr,
        _ => null
    };

    // Puts back each key and the password this request changed. One that can't be put back is removed when it was
    // typed for a new server, as the old address stays. False when any couldn't be put back.
    private bool PutBackSecrets((string? Sonarr, string? Radarr, string? Qbit) before, bool newSonarrHost, bool newRadarrHost, bool newQbitHost)
    {
        bool all = true;
        foreach (var (now, was, newHost, set) in new (string?, string?, bool, Action<string?>)[]
        {
            (_store.GetKey(ArrApp.Sonarr), before.Sonarr, newSonarrHost, key => _store.SetKey(ArrApp.Sonarr, key)),
            (_store.GetKey(ArrApp.Radarr), before.Radarr, newRadarrHost, key => _store.SetKey(ArrApp.Radarr, key)),
            (_store.GetQbitPassword(), before.Qbit, newQbitHost, _store.SetQbitPassword)
        })
        {
            if (string.Equals(now, was, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                set(was);
            }
            catch (Exception ex)
            {
                all = false;
                _logger.LogWarning(ex, "Medic Profiles: couldn't put back a key or password after a failed save");
                if (newHost)
                {
                    ForgetSecret(set);
                }
            }
        }

        return all;
    }

    private ObjectResult NotPutBack() =>
        Problem("Couldn't save the settings, and a key or password couldn't be put back as it was: type the keys and password again. Check the Jellyfin log for Medic Profiles errors.");

    private void ForgetSecret(Action<string?> set)
    {
        try
        {
            set(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: couldn't remove a key typed for a new server");
        }
    }

    // A different scheme, host or port; a first address (nothing saved before) isn't a change.
    private static bool HostChanged(string? oldUrl, string newUrl) =>
        !string.IsNullOrWhiteSpace(oldUrl) && !string.Equals(Authority(oldUrl), Authority(newUrl), StringComparison.OrdinalIgnoreCase);

    internal static string Authority(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}:{u.Port}" : url;

    private static string CleanUrl(string? url)
    {
        string u = (url ?? string.Empty).Trim().TrimEnd('/');
        if (u.Length > 0 && !u.Contains("://", StringComparison.Ordinal))
        {
            u = "http://" + u;
        }

        return u;
    }

    private ContentResult Json(object body) =>
        Content(JsonSerializer.Serialize(body, ArrStore.JsonOptions), MediaTypeNames.Application.Json);

    // What SaveSettings changes in the configuration, to put back when it can't be saved.
    private sealed record SavedSettings(string SonarrUrl, string RadarrUrl, int StuckAfterHours, string QbitUrl, string QbitUser, string[] QbitPatternsAdded, bool? QbitListWasOn, string QbitPatternsHost)
    {
        public static SavedSettings Of(PluginConfiguration c) =>
            new(c.SonarrUrl, c.RadarrUrl, c.StuckAfterHours, c.QbitUrl, c.QbitUser, c.QbitPatternsAdded, c.QbitListWasOn, c.QbitPatternsHost);

        public void PutBack(PluginConfiguration c)
        {
            c.SonarrUrl = SonarrUrl;
            c.RadarrUrl = RadarrUrl;
            c.StuckAfterHours = StuckAfterHours;
            c.QbitUrl = QbitUrl;
            c.QbitUser = QbitUser;
            c.QbitPatternsAdded = QbitPatternsAdded;
            c.QbitListWasOn = QbitListWasOn;
            c.QbitPatternsHost = QbitPatternsHost;
        }
    }

    public class SettingsRequest
    {
        public string? SonarrUrl { get; set; }

        public string? RadarrUrl { get; set; }

        public string? SonarrKey { get; set; }

        public string? RadarrKey { get; set; }

        public bool ClearSonarrKey { get; set; }

        public bool ClearRadarrKey { get; set; }

        public int? StuckAfterHours { get; set; }

        public string? QbitUrl { get; set; }

        public string? QbitUser { get; set; }

        public string? QbitPassword { get; set; }

        public bool ClearQbitPassword { get; set; }
    }

    public class RemoveRequest
    {
        public string? App { get; set; }

        public List<int>? QueueIds { get; set; }

        public bool Block { get; set; }

        public bool SearchAgain { get; set; } = true;

        // Leave it in the download client (still seeding); Sonarr or Radarr stops tracking it.
        public bool KeepInClient { get; set; }

        public string? Title { get; set; }
    }

    public class UnblockRequest
    {
        public string? App { get; set; }

        public int Id { get; set; }

        public string? Title { get; set; }
    }

    public class ImportRequest
    {
        public string? App { get; set; }

        public string DownloadId { get; set; } = string.Empty;

        public string? ImportMode { get; set; }

        public string? Title { get; set; }

        public List<ImportFileRequest>? Files { get; set; }
    }
}

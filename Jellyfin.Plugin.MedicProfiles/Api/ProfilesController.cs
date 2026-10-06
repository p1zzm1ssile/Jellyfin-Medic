using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public ProfilesController(ArrStore store, ArrClient arr, DownloadsService downloads, ProfileAdvisor advisor)
    {
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
            stuckAfterHours = config.StuckAfterHours
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
        plugin.Configuration.SonarrUrl = CleanUrl(request.SonarrUrl);
        plugin.Configuration.RadarrUrl = CleanUrl(request.RadarrUrl);
        plugin.Configuration.StuckAfterHours = Math.Clamp(request.StuckAfterHours ?? plugin.Configuration.StuckAfterHours, 1, 168);
        plugin.SaveConfiguration();

        // A blank key box means "keep the saved key"; Remove clears it.
        if (request.ClearSonarrKey)
        {
            _store.SetKey(ArrApp.Sonarr, null);
        }
        else if (!string.IsNullOrWhiteSpace(request.SonarrKey))
        {
            _store.SetKey(ArrApp.Sonarr, request.SonarrKey);
        }

        if (request.ClearRadarrKey)
        {
            _store.SetKey(ArrApp.Radarr, null);
        }
        else if (!string.IsNullOrWhiteSpace(request.RadarrKey))
        {
            _store.SetKey(ArrApp.Radarr, request.RadarrKey);
        }

        return NoContent();
    }

    [HttpPost("Test/{app}")]
    public async Task<ActionResult> Test([FromRoute] string app, CancellationToken ct)
    {
        if (ParseApp(app) is not { } a)
        {
            return BadRequest();
        }

        using var result = await _arr.GetAsync(a, "system/status", ct).ConfigureAwait(false);
        string? version = result.Json is { } doc ? Json(doc.RootElement, "version") : null;
        return Json(new
        {
            ok = result.Ok,
            message = result.Ok ? $"{ArrClient.Name(a)} answered{(version is null ? string.Empty : " (version " + version + ")")} and accepted the key." : result.Message
        });
    }

    // ---------- Downloads ----------

    [HttpGet("Queue")]
    public async Task<ActionResult> GetQueue(CancellationToken ct)
    {
        var (rows, errors) = await _downloads.QueueAsync(ct).ConfigureAwait(false);
        return Json(new { configured = Configured(), rows, errors });
    }

    [HttpPost("Queue/Remove")]
    public async Task<ActionResult> Remove([FromBody] RemoveRequest request, CancellationToken ct)
    {
        if (request is null || ParseApp(request.App) is not { } app)
        {
            return BadRequest();
        }

        var (ok, message) = await _downloads.RemoveAsync(app, request.QueueIds ?? new List<int>(), request.Block, request.SearchAgain, request.Title ?? "A download", UserName(), ct).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    [HttpGet("Blocked")]
    public async Task<ActionResult> GetBlocked(CancellationToken ct)
    {
        var (rows, errors) = await _downloads.BlockedAsync(ct).ConfigureAwait(false);
        return Json(new { configured = Configured(), rows, errors });
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
    public async Task<ActionResult> SearchLibrary([FromRoute] string app, [FromQuery] string q, CancellationToken ct)
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

    private static string CleanUrl(string? url)
    {
        string u = (url ?? string.Empty).Trim().TrimEnd('/');
        if (u.Length > 0 && !u.Contains("://", StringComparison.Ordinal))
        {
            u = "http://" + u;
        }

        return u;
    }

    private static string? Json(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private ContentResult Json(object body) =>
        Content(JsonSerializer.Serialize(body, ArrStore.JsonOptions), MediaTypeNames.Application.Json);

    public class SettingsRequest
    {
        public string? SonarrUrl { get; set; }

        public string? RadarrUrl { get; set; }

        public string? SonarrKey { get; set; }

        public string? RadarrKey { get; set; }

        public bool ClearSonarrKey { get; set; }

        public bool ClearRadarrKey { get; set; }

        public int? StuckAfterHours { get; set; }
    }

    public class RemoveRequest
    {
        public string? App { get; set; }

        public List<int>? QueueIds { get; set; }

        public bool Block { get; set; }

        public bool SearchAgain { get; set; } = true;

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

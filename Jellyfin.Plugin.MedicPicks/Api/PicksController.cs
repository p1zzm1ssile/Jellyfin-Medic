using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicPicks.Picks;
using Jellyfin.Plugin.MedicPicks.ScheduledTasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MedicPicks.Api;

/// <summary>
/// /MedicPicks/Me      any signed-in user, returns only their own picks
/// /MedicPicks/Page    the picks page (no data in it; it calls /Me with the viewer's own token)
/// /MedicPicks/Admin/* admins only
/// </summary>
[ApiController]
[Route("MedicPicks")]
public class PicksController : ControllerBase
{
    private const string AdminPolicy = "RequiresElevation";
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly PicksStore _store;
    private readonly ITaskManager _taskManager;
    private readonly IAuthorizationContext _authContext;
    private readonly PicksEngine _engine;
    private readonly SeerrClient _seerr;

    public PicksController(PicksStore store, ITaskManager taskManager, IAuthorizationContext authContext, PicksEngine engine, SeerrClient seerr)
    {
        _store = store;
        _taskManager = taskManager;
        _authContext = authContext;
        _engine = engine;
        _seerr = seerr;
    }

    [HttpGet("Me")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> GetMyPicks()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance!.Configuration;
        var picks = _store.Load(userId);

        var body = new
        {
            generatedUtc = picks?.GeneratedUtc,
            note = picks is null
                ? "Your picks haven't been built yet. They're made overnight, so check back tomorrow."
                : picks.Note,
            seerrUrl = string.IsNullOrWhiteSpace(config.JellyseerrUrl) ? null : config.JellyseerrUrl.TrimEnd('/'),
            // "direct": the button makes the request; "link": it opens Seerr; "none": it opens TMDb.
            requestMode = RequestMode(config),
            preferences = _store.LoadPreferences(userId),
            playlistName = config.CreatePlaylists && picks?.PlaylistId is not null ? config.PlaylistName : null,
            inLibrary = picks?.InLibrary ?? new System.Collections.Generic.List<LibraryPick>(),
            discover = picks?.Discover ?? new System.Collections.Generic.List<DiscoverPick>()
        };

        return Content(JsonSerializer.Serialize(body, PicksStore.JsonOptions), MediaTypeNames.Application.Json);
    }

    /// <summary>Saves a person's own choices and rebuilds their picks straight away.</summary>
    [HttpPost("Me/Preferences")]
    [Authorize]
    public async Task<ActionResult> SavePreferences([FromBody] UserPreferences prefs)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty || prefs is null)
        {
            return Unauthorized();
        }

        _store.SavePreferences(userId, prefs);
        _ = Task.Run(async () =>
        {
            try
            {
                await _engine.BuildForUserAsync(userId, _engine.CreateRunContext(), System.Threading.CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The nightly run will catch up.
            }
        });
        return NoContent();
    }

    /// <summary>Requests a title in Seerr as the signed-in person, using their own Seerr account.</summary>
    [HttpPost("Request")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> RequestTitle([FromBody] TitleRequest request, System.Threading.CancellationToken cancellationToken)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance!.Configuration;
        if (RequestMode(config) != "direct")
        {
            return BadRequest("Requests from this page aren't switched on.");
        }

        string type = request?.MediaType == "tv" ? "tv" : "movie";
        if (request is null || request.TmdbId <= 0)
        {
            return BadRequest();
        }

        var result = await _seerr.RequestAsync(config.JellyseerrUrl, userId, request.TmdbId, type, cancellationToken).ConfigureAwait(false);
        return Content(JsonSerializer.Serialize(result, PicksStore.JsonOptions), MediaTypeNames.Application.Json);
    }

    [HttpGet("Page")]
    [AllowAnonymous]
    public ActionResult GetPage()
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.MedicPicks.Web.picks.html");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), MediaTypeNames.Text.Html);
    }

    [HttpGet("Admin/Status")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult GetStatus()
    {
        var body = new
        {
            tmdbKeySet = _store.GetTmdbKey() is not null,
            seerrKeySet = _store.GetSeerrKey() is not null,
            usersWithPicks = _store.CountUsersWithPicks()
        };

        return Content(JsonSerializer.Serialize(body, PicksStore.JsonOptions), MediaTypeNames.Application.Json);
    }

    [HttpPost("Admin/TmdbKey")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult SetTmdbKey([FromBody] TmdbKeyRequest request)
    {
        _store.SetTmdbKey(string.IsNullOrWhiteSpace(request?.Key) ? null : request!.Key);
        return NoContent();
    }

    [HttpPost("Admin/SeerrKey")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult SetSeerrKey([FromBody] TmdbKeyRequest request)
    {
        _store.SetSeerrKey(string.IsNullOrWhiteSpace(request?.Key) ? null : request!.Key);
        return NoContent();
    }

    [HttpPost("Admin/TestSeerr")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult> TestSeerr(System.Threading.CancellationToken cancellationToken)
    {
        var (ok, message) = await _seerr.TestAsync(Plugin.Instance!.Configuration.JellyseerrUrl, cancellationToken).ConfigureAwait(false);
        return Content(JsonSerializer.Serialize(new { ok, message }, PicksStore.JsonOptions), MediaTypeNames.Application.Json);
    }

    [HttpPost("Admin/Refresh")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult Refresh()
    {
        _taskManager.QueueScheduledTask<BuildPicksTask>();
        return NoContent();
    }

    private async Task<Guid> GetUserIdAsync()
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type == UserIdClaim)?.Value;
        if (Guid.TryParse(claim, out var fromClaim) && fromClaim != Guid.Empty)
        {
            return fromClaim;
        }

        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.UserId;
    }

    public class TmdbKeyRequest
    {
        public string? Key { get; set; }
    }

    public class TitleRequest
    {
        public int TmdbId { get; set; }

        public string? MediaType { get; set; }
    }

    private string RequestMode(Configuration.PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.JellyseerrUrl))
        {
            return "none";
        }

        return config.SeerrDirectRequests && _store.GetSeerrKey() is not null ? "direct" : "link";
    }
}

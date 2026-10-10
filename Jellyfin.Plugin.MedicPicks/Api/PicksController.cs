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

    // People whose picks are being rebuilt after a change, and whether another change came in meanwhile.
    // A change during a rebuild doesn't start a second one: the running one goes round once more, so
    // repeated clicks can't pile up library scans and TMDb calls, and the latest choices still apply.
    private static readonly System.Collections.Generic.Dictionary<Guid, bool> Rebuilding = new();

    private readonly PicksStore _store;
    private readonly ITaskManager _taskManager;
    private readonly IAuthorizationContext _authContext;
    private readonly PicksEngine _engine;
    private readonly SeerrClient _seerr;
    private readonly RequestTracker _requests;

    public PicksController(PicksStore store, ITaskManager taskManager, IAuthorizationContext authContext, PicksEngine engine, SeerrClient seerr, RequestTracker requests)
    {
        _requests = requests;
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
            rebuilding = IsRebuilding(userId),
            note = picks is null
                ? "Your picks haven't been built yet. They're made overnight, so check back tomorrow."
                : picks.Note,
            seerrUrl = string.IsNullOrWhiteSpace(config.JellyseerrUrl) ? null : config.JellyseerrUrl.TrimEnd('/'),
            // "direct": the button makes the request; "link": it opens Seerr; "none": it opens TMDb.
            requestMode = RequestMode(config),
            preferences = _store.LoadPreferences(userId),
            availableGenres = Genres.ForChoices(picks?.AvailableGenres),
            seasonalGenres = Genres.Seasonal,
            countChoices = UserPreferences.CountChoices,
            defaultCount = Math.Clamp(config.LibraryPickCount, 1, 100),
            dubLanguage = AudioLanguage.FromTmdb(config.TmdbLanguage).Name,
            dubLanguageCode = AudioLanguage.FromTmdb(config.TmdbLanguage).TwoLetter,
            playlistName = config.CreatePlaylists && picks?.PlaylistId is not null ? config.PlaylistName : null,
            inLibrary = picks?.InLibrary ?? new System.Collections.Generic.List<LibraryPick>(),
            linked = picks?.Linked ?? new System.Collections.Generic.List<LibraryPick>(),
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
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (prefs is null)
        {
            return BadRequest();
        }

        // Hidden titles are only changed by Hide and Unhide, so keep the saved ones.
        var saved = _store.LoadPreferences(userId);
        prefs.HiddenItems = saved.HiddenItems;
        prefs.HiddenTmdb = saved.HiddenTmdb;
        // New choices start from the best matches again.
        prefs.SeenItems = new();
        prefs.SeenTmdb = new();
        _store.SavePreferences(userId, prefs.Normalise());
        StartRebuild(userId);
        return NoContent();
    }

    /// <summary>"Show me different ones": skips the picks shown now and rebuilds with the next best.</summary>
    [HttpPost("Me/More")]
    [Authorize]
    public async Task<ActionResult> ShowDifferent()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var prefs = _store.LoadPreferences(userId);
        var picks = _store.Load(userId);
        if (picks is not null)
        {
            prefs.SeenItems.AddRange(picks.InLibrary.Concat(picks.Linked).Select(p => p.ItemId));
            prefs.SeenTmdb.AddRange(picks.Discover.Select(p => (p.MediaType == "tv" ? "tv:" : "movie:") + p.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        _store.SavePreferences(userId, prefs.Normalise());
        StartRebuild(userId);
        return NoContent();
    }

    /// <summary>"Not interested": hides one title from this person's picks for good, straight away.</summary>
    [HttpPost("Me/Hide")]
    [Authorize]
    public async Task<ActionResult> HideTitle([FromBody] HideRequest request)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (request is null || (request.ItemId is null && (request.TmdbId is null or <= 0)))
        {
            return BadRequest();
        }

        var prefs = _store.LoadPreferences(userId);
        var picks = _store.Load(userId);
        if (request.ItemId is { } itemId)
        {
            prefs.HiddenItems.Add(itemId);
            picks?.InLibrary.RemoveAll(p => p.ItemId == itemId);
            picks?.Linked.RemoveAll(p => p.ItemId == itemId);
        }
        else
        {
            string key = (request.MediaType == "tv" ? "tv:" : "movie:") + request.TmdbId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            prefs.HiddenTmdb.Add(key);
            picks?.Discover.RemoveAll(p => (p.MediaType == "tv" ? "tv:" : "movie:") + p.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) == key);
        }

        _store.SavePreferences(userId, prefs.Normalise());
        if (picks is not null)
        {
            _store.Save(userId, picks);
        }

        RebuildAgainIfRunning(userId);
        return NoContent();
    }

    /// <summary>Shows every hidden title again, and rebuilds this person's picks.</summary>
    [HttpPost("Me/Unhide")]
    [Authorize]
    public async Task<ActionResult> UnhideAll()
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var prefs = _store.LoadPreferences(userId);
        prefs.HiddenItems.Clear();
        prefs.HiddenTmdb.Clear();
        _store.SavePreferences(userId, prefs);
        StartRebuild(userId);
        return NoContent();
    }

    private static bool IsRebuilding(Guid userId)
    {
        lock (Rebuilding)
        {
            return Rebuilding.ContainsKey(userId);
        }
    }

    private void StartRebuild(Guid userId)
    {
        lock (Rebuilding)
        {
            if (Rebuilding.ContainsKey(userId))
            {
                Rebuilding[userId] = true; // the running rebuild goes round again with the new choices
                return;
            }

            Rebuilding[userId] = false;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await _engine.BuildForUserAsync(userId, _engine.CreateRunContext(), System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The nightly run will catch up.
                }

                lock (Rebuilding)
                {
                    if (!Rebuilding[userId])
                    {
                        Rebuilding.Remove(userId);
                        return;
                    }

                    Rebuilding[userId] = false;
                }
            }
        });
    }

    /// <summary>If a rebuild is running for this person, it goes round again so it doesn't undo a change.</summary>
    private static void RebuildAgainIfRunning(Guid userId)
    {
        lock (Rebuilding)
        {
            if (Rebuilding.ContainsKey(userId))
            {
                Rebuilding[userId] = true;
            }
        }
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

    /// <summary>
    /// The signed-in person's own Seerr requests and where each has got to. Only ever their own:
    /// Seerr is asked for that one account's requests.
    /// </summary>
    [HttpGet("Me/Requests")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> GetMyRequests(System.Threading.CancellationToken cancellationToken)
    {
        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance!.Configuration;
        if (!TrackingRequests(config))
        {
            return Json(new { enabled = false });
        }

        var (rows, linked) = await _requests.ForUserAsync(config.JellyseerrUrl, userId, cancellationToken).ConfigureAwait(false);
        return Json(new
        {
            enabled = true,
            linked,
            reachable = rows is not null,
            requests = rows ?? new System.Collections.Generic.List<RequestRow>()
        });
    }

    /// <summary>Everyone's requests waiting for approval, for admins to approve or decline.</summary>
    [HttpGet("Admin/Requests")]
    [Authorize(Policy = AdminPolicy)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> GetPendingRequests(System.Threading.CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!TrackingRequests(config))
        {
            return Json(new { enabled = false });
        }

        var userId = await GetUserIdAsync().ConfigureAwait(false);
        var rows = await _requests.PendingAsync(config.JellyseerrUrl, userId, cancellationToken).ConfigureAwait(false);
        return Json(new
        {
            enabled = true,
            reachable = rows is not null,
            requests = rows ?? new System.Collections.Generic.List<RequestRow>()
        });
    }

    [HttpPost("Admin/Requests/{requestId}/Approve")]
    [Authorize(Policy = AdminPolicy)]
    public Task<ActionResult> ApproveRequest([FromRoute] int requestId, System.Threading.CancellationToken cancellationToken) =>
        SetRequestStatus(requestId, true, cancellationToken);

    [HttpPost("Admin/Requests/{requestId}/Decline")]
    [Authorize(Policy = AdminPolicy)]
    public Task<ActionResult> DeclineRequest([FromRoute] int requestId, System.Threading.CancellationToken cancellationToken) =>
        SetRequestStatus(requestId, false, cancellationToken);

    private async Task<ActionResult> SetRequestStatus(int requestId, bool approve, System.Threading.CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (requestId <= 0 || !TrackingRequests(config))
        {
            return BadRequest();
        }

        var (ok, message) = await _seerr.SetRequestStatusAsync(config.JellyseerrUrl, requestId, approve, cancellationToken).ConfigureAwait(false);
        return Json(new { ok, message });
    }

    private bool TrackingRequests(Configuration.PluginConfiguration config) =>
        config.ShowRequests && !string.IsNullOrWhiteSpace(config.JellyseerrUrl) && _store.GetSeerrKey() is not null;

    private ContentResult Json(object body) =>
        Content(JsonSerializer.Serialize(body, PicksStore.JsonOptions), MediaTypeNames.Application.Json);

    [HttpGet("Page")]
    [AllowAnonymous]
    public ActionResult GetPage()
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.MedicPicks.Web.picks.html");
        if (stream is null)
        {
            return NotFound();
        }

        // Always check for a newer page, so an update shows straight away instead of a cached copy.
        Response.Headers.CacheControl = "no-cache";
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

    public class HideRequest
    {
        public Guid? ItemId { get; set; }

        public int? TmdbId { get; set; }

        public string? MediaType { get; set; }
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

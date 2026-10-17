using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicPicks.Picks;
using Jellyfin.Plugin.MedicPicks.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    // A person's rebuilds start at least this far apart, so clicking away at the page can't keep the server
    // reading the library. Choices made meanwhile are saved and go into the next one.
    public static readonly TimeSpan MinRebuildGap = TimeSpan.FromSeconds(20);

    // When each person's last rebuild started (under the Rebuilding lock).
    private static readonly System.Collections.Generic.Dictionary<Guid, DateTime> LastRebuildUtc = new();

    private readonly PicksStore _store;
    private readonly ITaskManager _taskManager;
    private readonly IAuthorizationContext _authContext;
    private readonly ILocalizationManager _localization;
    private readonly PicksEngine _engine;
    private readonly SeerrClient _seerr;
    private readonly RequestTracker _requests;
    private readonly ILogger<PicksController> _logger;

    public PicksController(PicksStore store, ITaskManager taskManager, IAuthorizationContext authContext, ILocalizationManager localization, PicksEngine engine, SeerrClient seerr, RequestTracker requests, ILogger<PicksController> logger)
    {
        _requests = requests;
        _store = store;
        _taskManager = taskManager;
        _authContext = authContext;
        _localization = localization;
        _engine = engine;
        _seerr = seerr;
        _logger = logger;
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
        var dub = AudioLanguage.FromTmdb(config.TmdbLanguage, _localization.FindLanguageInfo);
        string requestMode = RequestMode(config);

        var body = new
        {
            generatedUtc = picks?.GeneratedUtc,
            rebuilding = IsRebuilding(userId),
            note = picks is null
                ? "Your picks haven't been built yet. They're made overnight, so check back tomorrow."
                : picks.Note,
            // Only links open Seerr; otherwise its address (often one inside Docker) stays on the server.
            seerrUrl = requestMode == "link" ? SeerrLink(config) : null,
            // "direct": the button makes the request; "link": it opens Seerr; "none": it opens TMDb.
            requestMode,
            preferences = _store.LoadPreferences(userId),
            availableGenres = Genres.ForChoices(picks?.AvailableGenres),
            seasonalGenres = Genres.Seasonal,
            countChoices = UserPreferences.CountChoices,
            defaultCount = Math.Clamp(config.LibraryPickCount, 1, 100),
            dubLanguage = dub.Name,
            dubLanguageCode = dub.TwoLetter,
            playlistName = config.CreatePlaylists && picks?.PlaylistId is not null ? config.PlaylistName : null,
            inLibrary = picks?.InLibrary ?? new System.Collections.Generic.List<LibraryPick>(),
            linked = picks?.Linked ?? new System.Collections.Generic.List<LibraryPick>(),
            // Saved picks stay until the next build, so Discover being switched off since then is checked here.
            discover = picks is not null && DiscoverShownTo(config, userId) ? picks.Discover : new System.Collections.Generic.List<DiscoverPick>()
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

        _store.UpdatePreferences(userId, saved =>
        {
            // Hidden titles are only changed by Hide and Unhide, so keep the saved ones.
            prefs.HiddenItems = saved.HiddenItems;
            prefs.HiddenTmdb = saved.HiddenTmdb;
            // New choices start from the best matches again.
            prefs.SeenItems = new();
            prefs.SeenTmdb = new();
            return prefs;
        });
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

        var picks = _store.Load(userId);
        // Discover picks that weren't on the page weren't seen, so they stay for when Discover is back.
        bool discoverShown = DiscoverShownTo(Plugin.Instance!.Configuration, userId);
        _store.UpdatePreferences(userId, prefs =>
        {
            if (picks is not null)
            {
                prefs.SeenItems.AddRange(picks.InLibrary.Concat(picks.Linked).Select(p => p.ItemId));
                if (discoverShown)
                {
                    prefs.SeenTmdb.AddRange(picks.Discover.Select(p => PicksStore.TmdbKey(p.MediaType, p.TmdbId)));
                }
            }

            return prefs;
        });
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

        _store.UpdatePreferences(userId, prefs =>
        {
            if (request.ItemId is { } itemId)
            {
                prefs.HiddenItems.Add(itemId);
            }
            else
            {
                prefs.HiddenTmdb.Add(PicksStore.TmdbKey(request.MediaType, request.TmdbId!.Value));
            }

            return prefs;
        });
        _store.RemoveHidden(userId);

        // A rebuild that started before this goes round again, to fill the gap; any rebuild, the nightly
        // one included, leaves hidden titles out when it saves.
        RebuildAgainIfRunning(userId);

        // The playlist follows within seconds rather than at the next build; quick hides share one refresh.
        if (request.ItemId is not null && !IsRebuilding(userId))
        {
            var engine = _engine;
            _ = Task.Run(async () =>
            {
                try
                {
                    await engine.RefreshPlaylistAsync(userId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Medic Picks: couldn't take a hidden title out of the playlist for user {UserId}", userId);
                }
            });
        }

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

        _store.UpdatePreferences(userId, prefs =>
        {
            prefs.HiddenItems.Clear();
            prefs.HiddenTmdb.Clear();
            return prefs;
        });
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
                    TimeSpan wait;
                    lock (Rebuilding)
                    {
                        wait = WaitBeforeRebuild(LastRebuildUtc.TryGetValue(userId, out var last) ? last : null, DateTime.UtcNow);
                    }

                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait).ConfigureAwait(false);
                    }

                    lock (Rebuilding)
                    {
                        LastRebuildUtc[userId] = DateTime.UtcNow;
                    }

                    await _engine.BuildForUserAsync(userId, _engine.CreateRebuildContext(), System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The nightly run will catch up, but say why this one didn't.
                    _logger.LogWarning(ex, "Medic Picks: couldn't rebuild picks for user {UserId}", userId);
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

    /// <summary>How long a rebuild waits so it starts at least MinRebuildGap after the person's last one.</summary>
    public static TimeSpan WaitBeforeRebuild(DateTime? lastUtc, DateTime nowUtc)
    {
        if (lastUtc is not { } last)
        {
            return TimeSpan.Zero;
        }

        var wait = last + MinRebuildGap - nowUtc;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
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

        // People without Discover (a child's account, say, or everyone when it's off) get no suggestions,
        // so they don't request from here either.
        if (!DiscoverShownTo(config, userId))
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "Requests from this page are turned off for your account.");
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

        var adminId = await GetUserIdAsync().ConfigureAwait(false);
        if (adminId == Guid.Empty)
        {
            return Unauthorized();
        }

        var (ok, message) = await _seerr.SetRequestStatusAsync(config.JellyseerrUrl, requestId, approve, adminId, cancellationToken).ConfigureAwait(false);
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
        // The page names itself to Jellyfin (in Dashboard, Devices) with the plugin's own version.
        string version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return Content(reader.ReadToEnd().Replace("__MEDIC_PICKS_VERSION__", version, StringComparison.Ordinal), MediaTypeNames.Text.Html);
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

    /// <summary>Rebuilds only these people's picks, for a settings change that affects nobody else.</summary>
    [HttpPost("Admin/Rebuild")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult Rebuild([FromBody] RebuildRequest request)
    {
        // Strings, as the settings page has the ids without dashes, the way Jellyfin lists users.
        var userIds = (request?.UserIds ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (userIds.Count == 0)
        {
            return BadRequest();
        }

        userIds.ForEach(StartRebuild);
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

    public class RebuildRequest
    {
        public string[]? UserIds { get; set; }
    }

    public class TitleRequest
    {
        public int TmdbId { get; set; }

        public string? MediaType { get; set; }
    }

    /// <summary>
    /// Whether this person gets Discover picks: switched on for the server, not unticked for them, and, for an
    /// account with parental limits, ticked by an admin.
    /// </summary>
    private bool DiscoverShownTo(Configuration.PluginConfiguration config, Guid userId)
    {
        var users = HttpContext?.RequestServices?.GetService(typeof(IUserManager)) as IUserManager;
        return DiscoverAccess.Allowed(config, userId, DiscoverAccess.HasParentalLimits(users?.GetUserById(userId)));
    }

    private string RequestMode(Configuration.PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.JellyseerrUrl))
        {
            return "none";
        }

        if (config.SeerrDirectRequests && _store.GetSeerrKey() is not null)
        {
            return "direct";
        }

        // A link is only offered with an address people can open.
        return SeerrLink(config) is null ? "none" : "link";
    }

    /// <summary>
    /// The Seerr address people's browsers open: the one for viewers when it's set, otherwise the one this
    /// server uses. Only http and https addresses become links (never javascript:, say).
    /// </summary>
    private static string? SeerrLink(Configuration.PluginConfiguration config)
    {
        string url = (string.IsNullOrWhiteSpace(config.SeerrLinkUrl) ? config.JellyseerrUrl ?? string.Empty : config.SeerrLinkUrl).Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? url.TrimEnd('/')
            : null;
    }
}

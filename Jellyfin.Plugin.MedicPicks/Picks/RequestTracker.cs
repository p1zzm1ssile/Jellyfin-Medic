using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>One request, as the My picks page shows it.</summary>
public class RequestRow
{
    public int Id { get; set; }

    public string MediaType { get; set; } = "movie";

    public int TmdbId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? PosterPath { get; set; }

    public bool Is4k { get; set; }

    public DateTime? RequestedUtc { get; set; }

    /// <summary>Who asked for it. Only filled in for admins.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>pending, declined, searching, queued, downloading, trouble, partial, available, removed.</summary>
    public string State { get; set; } = "searching";

    public string StateText { get; set; } = string.Empty;

    /// <summary>0–100 while downloading, when the size is known.</summary>
    public int? Progress { get; set; }

    /// <summary>When the download should finish, from Sonarr or Radarr.</summary>
    public DateTime? EtaUtc { get; set; }

    /// <summary>For series: which episode, or how many, are downloading.</summary>
    public string? Detail { get; set; }

    /// <summary>The Jellyfin item to play, only when this person can open it.</summary>
    public Guid? PlayItemId { get; set; }
}

/// <summary>
/// Where each Seerr request has got to: waiting for approval, looking for a download, downloading
/// (with progress and an ETA from Sonarr or Radarr, through Seerr), or ready to watch.
/// Seerr is read with the admin's key, so people only ever see their own requests, filtered here
/// by their own Seerr account; nothing is sent to anyone's browser but the rows themselves.
/// </summary>
public class RequestTracker
{
    // Finished requests (ready, declined, gone) drop off the list after this long.
    private static readonly TimeSpan KeepFinished = TimeSpan.FromDays(14);

    private static readonly HashSet<string> TroubleStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "warning", "failed", "downloadClientUnavailable"
    };

    private static readonly HashSet<string> WaitingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "paused", "delay"
    };

    private readonly SeerrClient _seerr;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public RequestTracker(SeerrClient seerr, ILibraryManager libraryManager, IUserManager userManager)
    {
        _seerr = seerr;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// This person's own requests. Null when Seerr couldn't be reached; an empty list with
    /// <paramref name="linked"/> false when they've never signed in to Seerr.
    /// </summary>
    public async Task<(List<RequestRow>? Rows, bool Linked)> ForUserAsync(string baseUrl, Guid jellyfinUserId, CancellationToken ct)
    {
        int? seerrUserId = await _seerr.FindSeerrUserAsync(baseUrl, jellyfinUserId, ct).ConfigureAwait(false);
        if (seerrUserId is null)
        {
            return (new List<RequestRow>(), false);
        }

        using var doc = await _seerr.GetRequestsAsync(baseUrl, seerrUserId, "all", 30, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return (null, true);
        }

        var rows = new List<RequestRow>();
        foreach (var request in Results(doc))
        {
            var row = await ToRowAsync(baseUrl, request, jellyfinUserId, includeRequester: false, ct).ConfigureAwait(false);
            if (row is not null && !FinishedLongAgo(row, request))
            {
                rows.Add(row);
            }
        }

        return (Ordered(rows), true);
    }

    /// <summary>Everyone's requests waiting for approval, for admins. Null when Seerr couldn't be reached.</summary>
    public async Task<List<RequestRow>?> PendingAsync(string baseUrl, Guid adminUserId, CancellationToken ct)
    {
        using var doc = await _seerr.GetRequestsAsync(baseUrl, null, "pending", 50, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var rows = new List<RequestRow>();
        foreach (var request in Results(doc))
        {
            var row = await ToRowAsync(baseUrl, request, adminUserId, includeRequester: true, ct).ConfigureAwait(false);
            if (row is not null && row.State == "pending")
            {
                rows.Add(row);
            }
        }

        return rows.OrderBy(r => r.RequestedUtc).ToList();
    }

    private static IEnumerable<JsonElement> Results(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private async Task<RequestRow?> ToRowAsync(string baseUrl, JsonElement request, Guid viewerId, bool includeRequester, CancellationToken ct)
    {
        if (!request.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int tmdbId = Int(media, "tmdbId") ?? 0;
        if (tmdbId <= 0)
        {
            return null;
        }

        bool is4k = Bool(request, "is4k");
        string type = (Text(request, "type") ?? Text(media, "mediaType")) == "tv" ? "tv" : "movie";
        var row = new RequestRow
        {
            Id = Int(request, "id") ?? 0,
            MediaType = type,
            TmdbId = tmdbId,
            Is4k = is4k,
            RequestedUtc = Date(request, "createdAt")
        };

        var title = await _seerr.GetTitleAsync(baseUrl, type, tmdbId, ct).ConfigureAwait(false);
        row.Title = title?.Name ?? (type == "tv" ? "A series" : "A film");
        row.Year = title?.Year;
        row.PosterPath = title?.PosterPath;

        if (includeRequester && request.TryGetProperty("requestedBy", out var by) && by.ValueKind == JsonValueKind.Object)
        {
            row.RequestedBy = Text(by, "displayName") ?? Text(by, "jellyfinUsername") ?? Text(by, "username");
        }

        int requestStatus = Int(request, "status") ?? 0;
        int mediaStatus = Int(media, is4k ? "status4k" : "status") ?? 1;
        var seasons = RequestedSeasons(request);
        var downloads = Downloads(media, is4k ? "downloadStatus4k" : "downloadStatus")
            .Where(d => seasons.Count == 0 || d.Season is null || seasons.Contains(d.Season.Value))
            .ToList();

        Describe(row, requestStatus, mediaStatus, downloads);

        if (row.State is "available" or "partial")
        {
            row.PlayItemId = PlayableFor(viewerId, Text(media, is4k ? "jellyfinMediaId4k" : "jellyfinMediaId"));
        }

        return row;
    }

    /// <summary>Seerr request status: 1 waiting, 2 approved, 3 declined, 4 failed, 5 completed.
    /// Media status: 2 pending, 3 processing, 4 partly available, 5 available, 6 blocklisted, 7 deleted.</summary>
    private static void Describe(RequestRow row, int requestStatus, int mediaStatus, List<Download> downloads)
    {
        if (requestStatus == 3)
        {
            Set(row, "declined", "Declined");
            return;
        }

        if (requestStatus == 1)
        {
            Set(row, "pending", "Waiting for approval");
            return;
        }

        if (mediaStatus == 5)
        {
            Set(row, "available", "Ready to watch");
            return;
        }

        if (downloads.Count > 0)
        {
            row.Detail = EpisodeDetail(downloads);
            if (downloads.Any(d => TroubleStatuses.Contains(d.Status)))
            {
                Set(row, "trouble", "There's a problem with the download. Whoever runs the server can sort it out.");
                return;
            }

            if (downloads.All(d => WaitingStatuses.Contains(d.Status)))
            {
                Set(row, "queued", "Found, waiting in the download queue");
                return;
            }

            long size = downloads.Sum(d => d.Size);
            long left = downloads.Sum(d => d.SizeLeft);
            row.Progress = size > 0 ? (int)Math.Clamp(Math.Round(100.0 * (size - left) / size), 0, 100) : null;
            var finishes = downloads.Select(d => d.Eta).Where(e => e is not null && e > DateTime.UtcNow).ToList();
            row.EtaUtc = finishes.Count > 0 ? finishes.Max() : null;
            Set(row, mediaStatus == 4 ? "partial" : "downloading", mediaStatus == 4 ? "Partly ready, the rest is downloading" : "Downloading");
            return;
        }

        if (requestStatus == 4)
        {
            Set(row, "trouble", "Seerr couldn't pass this on to be downloaded. Whoever runs the server can sort it out.");
            return;
        }

        if (mediaStatus == 4)
        {
            Set(row, "partial", "Partly ready to watch");
            return;
        }

        if (mediaStatus is 6 or 7)
        {
            Set(row, "removed", "No longer available");
            return;
        }

        Set(row, "searching", "Approved, looking for a download");
    }

    private static void Set(RequestRow row, string state, string text)
    {
        row.State = state;
        row.StateText = text;
    }

    private static string? EpisodeDetail(List<Download> downloads)
    {
        var episodes = downloads.Where(d => d.Season is not null && d.Episode is not null).ToList();
        if (episodes.Count == 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "Season {0}, episode {1}", episodes[0].Season, episodes[0].Episode);
        }

        return episodes.Count > 1
            ? episodes.Count.ToString(CultureInfo.InvariantCulture) + " episodes"
            : null;
    }

    /// <summary>The item, if it exists on this server and this person can open it.</summary>
    private Guid? PlayableFor(Guid userId, string? jellyfinMediaId)
    {
        if (!Guid.TryParse(jellyfinMediaId, out var itemId))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        var user = _userManager.GetUserById(userId);
        return item is not null && user is not null && item.IsVisible(user) ? itemId : null;
    }

    private static bool FinishedLongAgo(RequestRow row, JsonElement request)
    {
        if (row.State is not ("available" or "declined" or "removed"))
        {
            return false;
        }

        var changed = Date(request, "updatedAt") ?? row.RequestedUtc;
        return changed is not null && DateTime.UtcNow - changed.Value > KeepFinished;
    }

    // Problems first, then what's moving, then what's waiting, then what's done.
    private static List<RequestRow> Ordered(List<RequestRow> rows)
    {
        static int Rank(string state) => state switch
        {
            "trouble" => 0,
            "downloading" => 1,
            "partial" => 2,
            "queued" => 3,
            "searching" => 4,
            "pending" => 5,
            "available" => 6,
            _ => 7
        };

        return rows.Select((r, i) => (r, i)).OrderBy(x => Rank(x.r.State)).ThenBy(x => x.i).Select(x => x.r).ToList();
    }

    private static HashSet<int> RequestedSeasons(JsonElement request)
    {
        var seasons = new HashSet<int>();
        if (request.TryGetProperty("seasons", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in list.EnumerateArray())
            {
                if (Int(season, "seasonNumber") is { } n)
                {
                    seasons.Add(n);
                }
            }
        }

        return seasons;
    }

    private static IEnumerable<Download> Downloads(JsonElement media, string property)
    {
        if (!media.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var d in list.EnumerateArray())
        {
            int? season = null;
            int? episode = null;
            if (d.TryGetProperty("episode", out var ep) && ep.ValueKind == JsonValueKind.Object)
            {
                season = Int(ep, "seasonNumber");
                episode = Int(ep, "episodeNumber");
            }

            yield return new Download(
                Text(d, "status") ?? string.Empty,
                Long(d, "size"),
                Long(d, "sizeLeft"),
                Date(d, "estimatedCompletionTime"),
                season,
                episode);
        }
    }

    private sealed record Download(string Status, long Size, long SizeLeft, DateTime? Eta, int? Season, int? Episode);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (int)d : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (long)d : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}

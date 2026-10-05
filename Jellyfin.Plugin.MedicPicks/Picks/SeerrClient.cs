using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>Outcome of a request, worded for the person who pressed the button.</summary>
public class SeerrRequestResult
{
    // "requested", "approved", "exists", "unlinked", "refused", "error"
    public string Status { get; set; } = "error";

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Talks to Seerr (the merged Overseerr and Jellyseerr). Requests are made as the person who pressed
/// the button: Medic Picks finds their own Seerr account from their Jellyfin account, then asks Seerr
/// to act as that account. Seerr then applies that person's permissions, quotas and auto-approval,
/// exactly as if they'd requested it in Seerr themselves. The Seerr key never leaves the server.
/// </summary>
public class SeerrClient
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxPerWindow = 10;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PicksStore _store;
    private readonly ILogger<SeerrClient> _logger;
    private readonly ConcurrentDictionary<Guid, List<DateTime>> _recent = new();

    public SeerrClient(IHttpClientFactory httpClientFactory, PicksStore store, ILogger<SeerrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    /// <summary>Checks the address and key work. For the admin settings page.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string baseUrl, CancellationToken ct)
    {
        string? key = _store.GetSeerrKey();
        if (string.IsNullOrWhiteSpace(baseUrl) || key is null)
        {
            return (false, "Add the Seerr address and API key first.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(baseUrl, "/api/v1/auth/me"));
            request.Headers.Add("X-Api-Key", key);
            using var response = await Client().SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, "Seerr answered and accepted the key.")
                : (false, $"Seerr answered with {(int)response.StatusCode}. Check the API key (Seerr → Settings → General).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "Couldn't reach Seerr at that address from the Jellyfin server.");
        }
    }

    public async Task<SeerrRequestResult> RequestAsync(string baseUrl, Guid jellyfinUserId, int tmdbId, string mediaType, CancellationToken ct)
    {
        string? key = _store.GetSeerrKey();
        if (string.IsNullOrWhiteSpace(baseUrl) || key is null)
        {
            return new SeerrRequestResult { Message = "Requests from this page aren't set up yet. Ask whoever runs the server." };
        }

        if (!Allow(jellyfinUserId))
        {
            return new SeerrRequestResult { Status = "refused", Message = "That's a lot of requests in a minute. Try again shortly." };
        }

        try
        {
            // 1. Find this person's own Seerr account from their Jellyfin account.
            int? seerrUserId = await FindSeerrUserAsync(baseUrl, key, jellyfinUserId, ct).ConfigureAwait(false);
            if (seerrUserId is null)
            {
                return new SeerrRequestResult
                {
                    Status = "unlinked",
                    Message = "Your account isn't in Seerr yet. Sign in to Seerr once with your Jellyfin login, then try again."
                };
            }

            // 2. Ask Seerr to make the request as that person.
            var body = new Dictionary<string, object> { ["mediaType"] = mediaType, ["mediaId"] = tmdbId };
            if (mediaType == "tv")
            {
                body["seasons"] = "all";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, "/api/v1/request"))
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Api-Key", key);
            request.Headers.Add("X-API-User", seerrUserId.Value.ToString(CultureInfo.InvariantCulture));

            using var response = await Client().SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? seerrMessage = MessageFrom(text);

            if (response.IsSuccessStatusCode)
            {
                // Seerr request status: 1 = waiting for approval, 2 = approved.
                bool approved = StatusFrom(text) == 2;
                return new SeerrRequestResult
                {
                    Status = approved ? "approved" : "requested",
                    Message = approved ? "Requested and approved. It'll be added soon." : "Requested. It's waiting for approval."
                };
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return new SeerrRequestResult { Status = "exists", Message = "Already requested." };
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new SeerrRequestResult
                {
                    Status = "refused",
                    Message = string.IsNullOrWhiteSpace(seerrMessage)
                        ? "Seerr didn't allow this request. You may have reached your request limit."
                        : "Seerr says: " + seerrMessage
                };
            }

            _logger.LogWarning("Medic Picks: Seerr returned {Status} for a request: {Message}", (int)response.StatusCode, seerrMessage);
            return new SeerrRequestResult
            {
                Message = string.IsNullOrWhiteSpace(seerrMessage) ? "Seerr couldn't take the request just now." : "Seerr says: " + seerrMessage
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Medic Picks: couldn't reach Seerr");
            return new SeerrRequestResult { Message = "Couldn't reach Seerr just now. Try again later." };
        }
    }

    private async Task<int?> FindSeerrUserAsync(string baseUrl, string key, Guid jellyfinUserId, CancellationToken ct)
    {
        // Seerr stores Jellyfin IDs without dashes; try that first, then with.
        foreach (var id in new[] { jellyfinUserId.ToString("N"), jellyfinUserId.ToString("D") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(baseUrl, "/api/v1/user/jellyfin/" + id));
            request.Headers.Add("X-Api-Key", key);
            using var response = await Client().SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var seerrId))
            {
                return seerrId;
            }
        }

        return null;
    }

    private bool Allow(Guid userId)
    {
        var now = DateTime.UtcNow;
        var list = _recent.GetOrAdd(userId, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(t => now - t > Window);
            if (list.Count >= MaxPerWindow)
            {
                return false;
            }

            list.Add(now);
            return true;
        }
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(NamedClient.Default);

    private static string Url(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

    private static string? MessageFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? StatusFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }
}

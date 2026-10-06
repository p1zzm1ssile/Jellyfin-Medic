using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

public enum ArrApp
{
    Sonarr,
    Radarr
}

/// <summary>The answer from Sonarr or Radarr: the JSON when it worked, or a message worded for the admin.</summary>
public sealed class ArrResult : IDisposable
{
    public bool Ok { get; init; }

    public int Status { get; init; }

    public JsonDocument? Json { get; init; }

    public string Message { get; init; } = string.Empty;

    public void Dispose() => Json?.Dispose();
}

/// <summary>
/// Talks to Sonarr and Radarr's v3 API from the Jellyfin server. The keys stay on the server.
/// </summary>
public class ArrClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArrStore _store;
    private readonly ILogger<ArrClient> _logger;

    public ArrClient(IHttpClientFactory httpClientFactory, ArrStore store, ILogger<ArrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    public static string Name(ArrApp app) => app == ArrApp.Sonarr ? "Sonarr" : "Radarr";

    public static string? BaseUrl(ArrApp app)
    {
        var config = Plugin.Instance?.Configuration;
        string? url = app == ArrApp.Sonarr ? config?.SonarrUrl : config?.RadarrUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }

    /// <summary>Set up: an address and a key.</summary>
    public bool IsConfigured(ArrApp app) => BaseUrl(app) is not null && _store.GetKey(app) is not null;

    public Task<ArrResult> GetAsync(ArrApp app, string pathAndQuery, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Get, pathAndQuery, null, ct);

    public Task<ArrResult> PostAsync(ArrApp app, string path, object body, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Post, path, JsonSerializer.Serialize(body, ArrStore.JsonOptions), ct);

    public Task<ArrResult> PostRawAsync(ArrApp app, string path, string json, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Post, path, json, ct);

    public Task<ArrResult> DeleteAsync(ArrApp app, string pathAndQuery, object? body, CancellationToken ct) =>
        SendAsync(app, HttpMethod.Delete, pathAndQuery, body is null ? null : JsonSerializer.Serialize(body, ArrStore.JsonOptions), ct);

    private async Task<ArrResult> SendAsync(ArrApp app, HttpMethod method, string pathAndQuery, string? json, CancellationToken ct)
    {
        string? baseUrl = BaseUrl(app);
        string? key = _store.GetKey(app);
        if (baseUrl is null || key is null)
        {
            return new ArrResult { Message = $"{Name(app)} isn't set up yet. Add its address and API key on the Settings tab." };
        }

        try
        {
            using var request = new HttpRequestMessage(method, baseUrl + "/api/v3/" + pathAndQuery.TrimStart('/'));
            request.Headers.Add("X-Api-Key", key);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new ArrResult { Ok = true, Status = status, Json = text.Length == 0 ? null : TryParse(text) };
            }

            string message = status switch
            {
                401 => $"{Name(app)} didn't accept the API key. Copy it again from {Name(app)} → Settings → General.",
                404 => $"{Name(app)} says that wasn't found. It may have been removed already.",
                _ => ErrorFrom(text) is { } err ? $"{Name(app)} says: {err}" : $"{Name(app)} answered with {status}."
            };
            return new ArrResult { Status = status, Message = message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Medic Profiles: couldn't reach {App}", Name(app));
            return new ArrResult { Message = $"Couldn't reach {Name(app)} at {baseUrl} from the Jellyfin server." };
        }
    }

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Sonarr and Radarr send either {"message": "..."} or a list of validation errors.
    private static string? ErrorFrom(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                && root[0].TryGetProperty("errorMessage", out var e) && e.ValueKind == JsonValueKind.String)
            {
                return e.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

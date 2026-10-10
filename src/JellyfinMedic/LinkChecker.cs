using System.Collections.Concurrent;

namespace JellyfinMedic.Services;

/// <summary>An address worth checking: a plugin repository or a script an add-on loads.</summary>
public sealed class LinkTarget
{
    public string Kind { get; set; } = string.Empty; // "repository" or "script"

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Checks that addresses respond. Results are kept for 10 minutes so opening the report
/// repeatedly doesn't keep hitting the same sites.
/// </summary>
public static class LinkChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, (DateTime CheckedUtc, string Result, int Fails)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    // A link is only reported as failing after it misses twice in a row, so one blip isn't called "dead".
    private const int FailuresBeforeReporting = 2;

    /// <summary>Returns "OK", or a short reason such as "HTTP 404" or "No response".</summary>
    public static async Task<Dictionary<string, string>> CheckAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var targets = urls
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.Ordinal)
            .Take(25)
            .ToList();

        await Task.WhenAll(targets.Select(url => CheckOneAsync(url, ct))).ConfigureAwait(false);

        // Report a problem only once it has failed twice in a row; a single miss reads as "OK" for now.
        return targets.ToDictionary(
            u => u,
            u => Cache.TryGetValue(u, out var hit) ? (hit.Result == "OK" || hit.Fails < FailuresBeforeReporting ? "OK" : hit.Result) : "Not checked",
            StringComparer.Ordinal);
    }

    private static async Task CheckOneAsync(string url, CancellationToken ct)
    {
        bool knownGood = Cache.TryGetValue(url, out var hit) && hit.Result == "OK";
        if (hit.CheckedUtc != default && DateTime.UtcNow - hit.CheckedUtc < KeepFor && knownGood)
        {
            return; // A recent success is trusted for the full cache window.
        }

        // Try once; if it fails, try again after a moment before counting the failure, since
        // a site can briefly hiccup. Two tries here, plus the twice-in-a-row rule, means a
        // repository is only flagged when it's genuinely not responding.
        string result = await TryOnceAsync(url, ct).ConfigureAwait(false);
        if (result != "OK" && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            result = await TryOnceAsync(url, ct).ConfigureAwait(false);
        }

        int fails = result == "OK" ? 0 : (Cache.TryGetValue(url, out var prev) ? prev.Fails : 0) + 1;
        Cache[url] = (DateTime.UtcNow, result, fails);
    }

    private static async Task<string> TryOnceAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("JellyfinMedic/1.0 (Jellyfin plugin)");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? "OK" : $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "No response";
        }
        catch (HttpRequestException ex)
        {
            return "Unreachable" + (ex.StatusCode is { } code ? $" (HTTP {(int)code})" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Couldn't check (" + ex.GetType().Name + ")";
        }
    }
}

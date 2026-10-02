using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
    private static readonly ConcurrentDictionary<string, (DateTime CheckedUtc, string Result)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>Returns "OK", or a short reason such as "HTTP 404" or "No response".</summary>
    public static async Task<Dictionary<string, string>> CheckAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var targets = urls
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.Ordinal)
            .Take(25)
            .ToList();

        await Task.WhenAll(targets.Select(url => CheckOneAsync(url, ct))).ConfigureAwait(false);
        return targets.ToDictionary(u => u, u => Cache.TryGetValue(u, out var hit) ? hit.Result : "Not checked", StringComparer.Ordinal);
    }

    private static async Task CheckOneAsync(string url, CancellationToken ct)
    {
        if (Cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.CheckedUtc < KeepFor)
        {
            return;
        }

        string result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("JellyfinMedic/1.0 (Jellyfin plugin)");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            result = response.IsSuccessStatusCode ? "OK" : $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = "No response";
        }
        catch (HttpRequestException ex)
        {
            result = "Unreachable" + (ex.StatusCode is { } code ? $" (HTTP {(int)code})" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = "Couldn't check (" + ex.GetType().Name + ")";
        }

        Cache[url] = (DateTime.UtcNow, result);
    }
}

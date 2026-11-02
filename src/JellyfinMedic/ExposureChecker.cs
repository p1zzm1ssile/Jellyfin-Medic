using System.Net;

namespace JellyfinMedic.Services;

public class ExposureDefaults
{
    public string? Domain { get; set; }

    public string? PublicIp { get; set; }

    public int Port { get; set; } = 443;

    public string Protocol { get; set; } = "https";
}

public class ExposureResult
{
    public bool Checked { get; set; }

    public bool Reachable { get; set; }

    public bool RespondedAsJellyfin { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Protocol { get; set; } = "https";

    /// <summary>
    /// The address the result is about, as protocol://host:port with Jellyfin's base URL when it was asked for:
    /// where Jellyfin answered, or else the first address tried. Empty when the input was refused.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// True when the answer came from connecting from this server itself, not from a check out on the internet.
    /// That can differ: a router may not reach its own public address from inside, or a domain may point home.
    /// </summary>
    public bool FromThisServer { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Checks whether Jellyfin answers at a given address, port and protocol. With a domain (or plain http) it
/// connects from this server and sees whether the address responds as a Jellyfin server; for an IP over
/// https, or when that fails, it asks a public port-checker. Only the address and port are sent out; no
/// server data leaves your network. Nothing goes out until the admin asks: the check runs on "Check now",
/// and the public IP is looked up (at most once an hour) only for "Use my IP". From this server, Jellyfin
/// is looked for under its base URL and, if it isn't there, once at the root.
/// </summary>
public static class ExposureChecker
{
    private const string InfoPath = "/System/Info/Public";

    // Redirects aren't followed: an answer of any kind already means something is listening there.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly TimeSpan PublicIpCacheTime = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoPublicIpCacheTime = TimeSpan.FromMinutes(5);
    private static PublicIpLookup? _publicIp;

    private static readonly string[] IpServices =
    {
        "https://api.ipify.org",
        "https://ipv4.icanhazip.com",
        "https://checkip.amazonaws.com"
    };

    public static async Task<ExposureDefaults> DefaultsAsync(object? networkConfig, bool lookUpPublicIp, CancellationToken ct)
    {
        var defaults = new ExposureDefaults
        {
            // Only when asked for: finding it means asking a service out on the internet.
            PublicIp = lookUpPublicIp ? await PublicIpAsync(ct).ConfigureAwait(false) : null
        };

        if (PublishedAddress(networkConfig) is { } published)
        {
            defaults.Domain = published.Host;
            defaults.Protocol = published.Scheme;
            defaults.Port = published.Port;
        }

        return defaults;
    }

    // The address Jellyfin gives clients outside your network, read from its "Published Server URI" entries ("identifier=url").
    // An "all" entry overrides every other one in Jellyfin, so it comes first; "internal" and subnet entries
    // point at the local network, so they don't count.
    private static Uri? PublishedAddress(object? networkConfig) =>
        (SettingsReader.List(networkConfig, "PublishedServerUriBySubnet") ?? new List<string>())
            .Select(entry => entry.Split('='))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Identifier: parts[0].Trim().ToLowerInvariant(), Address: parts[1].Trim()))
            .Where(entry => entry.Identifier is "all" or "external")
            .OrderBy(entry => entry.Identifier == "all" ? 0 : 1)
            .Select(entry => WebAddress(entry.Address))
            .FirstOrDefault(url => url is not null);

    // Jellyfin takes an address without http:// or https:// as a host name or IP.
    private static Uri? WebAddress(string address) =>
        Uri.TryCreate(address.Contains("://") ? address : "https://" + address, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;

    public static async Task<ExposureResult> CheckAsync(string host, int port, string protocol, string? baseUrl, CancellationToken ct)
    {
        protocol = protocol?.ToLowerInvariant() == "http" ? "http" : "https";
        var result = new ExposureResult { Host = host?.Trim() ?? string.Empty, Port = port, Protocol = protocol };

        if (string.IsNullOrWhiteSpace(result.Host))
        {
            result.Error = "Enter a domain or IP address to check.";
            return result;
        }

        if (port is < 1 or > 65535)
        {
            result.Error = "That doesn't look like a valid port.";
            return result;
        }

        // Only a host name or an address: anything else (a path, a query, a second address) is refused here,
        // instead of ending up in the URL.
        string bare = result.Host.Trim('[', ']');
        if (Uri.CheckHostName(bare) == UriHostNameType.Unknown
            || !Uri.TryCreate(new UriBuilder(protocol, bare, port, InfoPath).ToString(), UriKind.Absolute, out var atRoot))
        {
            result.Error = "That doesn't look like a domain or IP address.";
            return result;
        }

        // Jellyfin with a base URL (such as /jellyfin) answers only under it: any other path is redirected to its web page.
        string basePath = baseUrl?.Trim().Trim('/') ?? string.Empty;
        var url = basePath.Length == 0 ? atRoot : new UriBuilder(atRoot) { Path = $"/{basePath}{InfoPath}" }.Uri;
        result.Url = Address(atRoot, basePath);

        // A domain (or plain http): connect from this server, the way a viewer's app would.
        bool looksLikeIp = IPAddress.TryParse(bare, out _);
        if (!looksLikeIp || protocol == "http")
        {
            result.FromThisServer = true;
            try
            {
                result.RespondedAsJellyfin = await AnswersAsJellyfinAsync(url, ct).ConfigureAwait(false);
                result.Checked = true;
                result.Reachable = true;

                // A reverse proxy can serve Jellyfin's base URL at the root of its own address.
                if (!result.RespondedAsJellyfin && basePath.Length > 0)
                {
                    result.RespondedAsJellyfin = await AnswersAsJellyfinAsync(atRoot, ct).ConfigureAwait(false);
                    if (result.RespondedAsJellyfin)
                    {
                        result.Url = Address(atRoot, string.Empty);
                    }
                }

                return result;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                // A certificate this server doesn't trust (self-signed, say): something is listening there all the same.
                result.Checked = true;
                result.Reachable = true;
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Only the try at the root failed: the address had already answered.
                if (result.Reachable)
                {
                    return result;
                }

                // Nothing answered from here. For a domain that is the result, though it only tells about this server's view.
                if (!looksLikeIp)
                {
                    result.Checked = true;
                    result.Reachable = false;
                    return result;
                }

                // For an IP, ask the port-checker from outside instead.
                result.FromThisServer = false;
            }
        }

        // A raw IP and port: ask a public port-checker whether it answers. It only sees the port, never a path.
        result.Url = Address(atRoot, string.Empty);
        try
        {
            string query = System.Text.Json.JsonSerializer.Serialize(new { host = bare, ports = new[] { port.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://portchecker.io/api/query")
            {
                Content = new StringContent(query, System.Text.Encoding.UTF8, "application/json")
            };
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).ToLowerInvariant();
                int pos = body.IndexOf($"\"{port}\"", StringComparison.Ordinal);
                if (pos < 0)
                {
                    pos = body.IndexOf($":{port},", StringComparison.Ordinal);
                }

                if (pos >= 0)
                {
                    string after = body[pos..Math.Min(body.Length, pos + 60)];
                    result.Checked = true;
                    result.Reachable = after.Contains("\"status\":true") || after.Contains("open");
                    return result;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing worked.
        }

        result.Error = "Couldn't test that address, so this isn't conclusive. Your network may block the check, or the port-check service may be down. Try again later.";
        return result;
    }

    // The port is always written out, so the page shows the one that was tested even when it is the scheme's default.
    private static string Address(Uri url, string basePath) =>
        $"{url.Scheme}://{url.Host}:{url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(basePath.Length == 0 ? string.Empty : "/" + basePath)}";

    // Throws, as HttpClient does, when nothing answers: the caller tells that apart from an answer that isn't Jellyfin.
    private static async Task<bool> AnswersAsJellyfinAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Contains("\"ServerName\"", StringComparison.OrdinalIgnoreCase) || body.Contains("\"Version\"", StringComparison.OrdinalIgnoreCase) || body.Contains("\"Id\"", StringComparison.OrdinalIgnoreCase);
    }

    // Kept for a while so pressing the button again doesn't ask an outside service each time. A failed
    // lookup is kept for less, so a short network blip doesn't hide the address for an hour.
    private static async Task<string?> PublicIpAsync(CancellationToken ct)
    {
        var known = _publicIp;
        if (known is not null && DateTime.UtcNow - known.At < (known.Ip is null ? NoPublicIpCacheTime : PublicIpCacheTime))
        {
            return known.Ip;
        }

        string? ip = await LookUpPublicIpAsync(ct).ConfigureAwait(false);
        if (!ct.IsCancellationRequested)
        {
            _publicIp = new PublicIpLookup(ip, DateTime.UtcNow);
        }

        return ip;
    }

    private static async Task<string?> LookUpPublicIpAsync(CancellationToken ct)
    {
        foreach (var service in IpServices)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                string ip = (await Http.GetStringAsync(service, timeout.Token).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(ip, out _))
                {
                    return ip;
                }
            }
            catch
            {
                // Try the next service.
            }
        }

        return null;
    }

    // A class, not a tuple: requests can read it while another one replaces it, and a reference swap is atomic.
    private sealed record PublicIpLookup(string? Ip, DateTime At);
}

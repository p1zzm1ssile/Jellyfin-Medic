using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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

    public string? Error { get; set; }
}

/// <summary>
/// Checks whether Jellyfin answers from the open internet at a given address, port and protocol.
/// With a domain it connects over the internet and sees whether the address responds as a Jellyfin
/// server; for an IP and a raw port it uses a public port-checker. Only the address and port are
/// sent out; no server data leaves your network. Runs only when the admin presses the button.
/// </summary>
public static class ExposureChecker
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly string[] IpServices =
    {
        "https://api.ipify.org",
        "https://ipv4.icanhazip.com",
        "https://checkip.amazonaws.com"
    };

    public static async Task<ExposureDefaults> DefaultsAsync(object? networkConfig, CancellationToken ct)
    {
        var defaults = new ExposureDefaults
        {
            PublicIp = await PublicIpAsync(ct).ConfigureAwait(false)
        };

        // Try to suggest the domain from the published server URL, if one is set.
        string? published = SettingsReader.Text(networkConfig, "PublishedServerUriBySubnet")
                            ?? SettingsReader.Text(networkConfig, "CertificateHost")
                            ?? SettingsReader.Text(networkConfig, "BaseUrl");
        if (!string.IsNullOrWhiteSpace(published) && Uri.TryCreate(published.Contains("://") ? published : "https://" + published, UriKind.Absolute, out var uri))
        {
            defaults.Domain = uri.Host;
        }

        return defaults;
    }

    public static async Task<ExposureResult> CheckAsync(string host, int port, string protocol, CancellationToken ct)
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

        // A domain (or any host over http/https): try to reach it the way a viewer would.
        bool looksLikeIp = IPAddress.TryParse(result.Host, out _);
        if (!looksLikeIp || protocol == "http")
        {
            try
            {
                string url = $"{protocol}://{result.Host}:{port}/System/Info/Public";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                result.Checked = true;
                result.Reachable = true;
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                result.RespondedAsJellyfin = body.Contains("\"ServerName\"", StringComparison.OrdinalIgnoreCase) || body.Contains("\"Version\"", StringComparison.OrdinalIgnoreCase) || body.Contains("\"Id\"", StringComparison.OrdinalIgnoreCase);
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Couldn't connect: for a domain that means nothing is answering there.
                if (!looksLikeIp)
                {
                    result.Checked = true;
                    result.Reachable = false;
                    return result;
                }
                // For an IP, fall through to the port-checker.
            }
        }

        // A raw IP and port: ask a public port-checker whether it answers.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://portchecker.io/api/query")
            {
                Content = new StringContent($"{{\"host\":\"{result.Host}\",\"ports\":[\"{port}\"]}}", System.Text.Encoding.UTF8, "application/json")
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

    private static async Task<string?> PublicIpAsync(CancellationToken ct)
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
}

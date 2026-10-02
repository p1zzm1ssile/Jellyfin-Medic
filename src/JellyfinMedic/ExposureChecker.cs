using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinMedic.Services;

public class ExposureResult
{
    public bool Checked { get; set; }

    public bool Reachable { get; set; }

    public int Port { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Asks one public service whether your Jellyfin port answers from the open internet. It sends
/// only the port number; no server data leaves your network. Used only when the admin presses the
/// button. The result isn't saved.
/// </summary>
public static class ExposureChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<ExposureResult> CheckAsync(int port, CancellationToken ct)
    {
        var result = new ExposureResult { Port = port };
        if (port is < 1 or > 65535)
        {
            result.Error = "That doesn't look like a valid port.";
            return result;
        }

        try
        {
            // A public open-port tester. It sees only the requester's address and the port.
            using var response = await Http.GetAsync($"https://ports.yougetsignal.com/check-port.php?remoteAddress=&portNumber={port}", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            result.Checked = true;
            result.Reachable = body.Contains("open", StringComparison.OrdinalIgnoreCase) && !body.Contains("closed", StringComparison.OrdinalIgnoreCase);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            result.Error = "Couldn't reach the port-check service. Your firewall or network may be blocking it.";
            return result;
        }
    }
}

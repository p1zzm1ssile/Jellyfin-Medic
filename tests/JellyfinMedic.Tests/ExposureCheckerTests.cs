using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JellyfinMedic.Services;
using MediaBrowser.Common.Net;

namespace JellyfinMedic.Tests;

public sealed class ExposureCheckerTests : IDisposable
{
    // A name rather than 127.0.0.1: a name that gets no answer ends the check there, where an address
    // would go on to ask the public port-checker.
    private const string Host = "localhost";

    private const string InfoPath = "/System/Info/Public";

    // Part of what Jellyfin answers at /System/Info/Public.
    private const string PublicInfo = "{\"ServerName\":\"medic-tests\",\"Version\":\"12.1.0\",\"ProductName\":\"Jellyfin Server\",\"Id\":\"5d1ef8f1a6b54d3c9b0d8c4b6f0e2a71\"}";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<string> _asked = new();

    [Fact]
    public async Task A_server_with_a_base_URL_answers_as_Jellyfin_under_it()
    {
        int port = Serve(jellyfinAt: "/jellyfin" + InfoPath);

        var result = await ExposureChecker.CheckAsync(Host, port, "http", "/jellyfin", CancellationToken.None);

        Assert.True(result.RespondedAsJellyfin);
        Assert.Equal($"http://localhost:{port}/jellyfin", result.Url);
        Assert.Equal(new[] { "/jellyfin" + InfoPath }, _asked);
    }

    [Fact]
    public async Task A_proxy_that_serves_Jellyfin_at_the_root_answers_as_Jellyfin_on_the_one_try_there()
    {
        int port = Serve(jellyfinAt: InfoPath);

        var result = await ExposureChecker.CheckAsync(Host, port, "http", "/jellyfin", CancellationToken.None);

        Assert.True(result.RespondedAsJellyfin);
        Assert.Equal($"http://localhost:{port}", result.Url);
        Assert.Equal(new[] { "/jellyfin" + InfoPath, InfoPath }, _asked);
    }

    [Fact]
    public async Task A_server_that_is_Jellyfin_neither_under_the_base_URL_nor_at_the_root_is_not_reported_as_Jellyfin()
    {
        int port = Serve(jellyfinAt: null);

        var result = await ExposureChecker.CheckAsync(Host, port, "http", "/jellyfin", CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.False(result.RespondedAsJellyfin);
        Assert.Equal($"http://localhost:{port}/jellyfin", result.Url);
        Assert.Equal(new[] { "/jellyfin" + InfoPath, InfoPath }, _asked);
    }

    [Fact]
    public async Task An_address_that_answered_under_the_base_URL_stays_reachable_when_the_try_at_the_root_fails()
    {
        int port = Serve(jellyfinAt: null, brokenAt: InfoPath);

        var result = await ExposureChecker.CheckAsync(Host, port, "http", "/jellyfin", CancellationToken.None);

        Assert.True(result.Checked);
        Assert.True(result.Reachable);
        Assert.False(result.RespondedAsJellyfin);
        Assert.Equal(new[] { "/jellyfin" + InfoPath, InfoPath }, _asked);
    }

    [Fact]
    public async Task Without_a_base_URL_only_the_root_is_asked_and_only_once()
    {
        int port = Serve(jellyfinAt: null);

        var result = await ExposureChecker.CheckAsync(Host, port, "http", string.Empty, CancellationToken.None);

        Assert.False(result.RespondedAsJellyfin);
        Assert.Equal($"http://localhost:{port}", result.Url);
        Assert.Equal(new[] { InfoPath }, _asked);
    }

    [Theory]
    [InlineData("all=https://jellyfin.example.com", "https", 443)]
    [InlineData("external=http://jellyfin.example.com:8096", "http", 8096)]
    [InlineData("all=jellyfin.example.com", "https", 443)]
    public async Task The_defaults_offer_the_address_Jellyfin_publishes_to_everyone_or_to_the_internet(string entry, string protocol, int port)
    {
        var defaults = await DefaultsFor(entry);

        Assert.Equal("jellyfin.example.com", defaults.Domain);
        Assert.Equal(protocol, defaults.Protocol);
        Assert.Equal(port, defaults.Port);
    }

    [Theory]
    [InlineData("internal=http://192.168.1.10:8096")]
    [InlineData("192.168.1.0/24=http://192.168.1.10:8096")]
    public async Task An_address_published_only_to_the_local_network_is_not_offered(string entry)
    {
        var defaults = await DefaultsFor(entry);

        Assert.Null(defaults.Domain);
        Assert.Equal("https", defaults.Protocol);
        Assert.Equal(443, defaults.Port);
    }

    [Theory]
    [InlineData("all=https://")]
    [InlineData("all=ftp://files.example.com")]
    public async Task An_entry_whose_address_does_not_parse_as_http_or_https_is_skipped(string broken)
    {
        var defaults = await DefaultsFor(broken, "external=https://jellyfin.example.com");

        Assert.Equal("jellyfin.example.com", defaults.Domain);
    }

    [Fact]
    public async Task An_all_entry_wins_over_an_external_one_as_it_does_in_Jellyfin()
    {
        var defaults = await DefaultsFor("external=https://outside.example.com", "all=https://jellyfin.example.com");

        Assert.Equal("jellyfin.example.com", defaults.Domain);
    }

    [Fact]
    public async Task A_server_that_publishes_no_address_gets_no_domain()
    {
        var defaults = await ExposureChecker.DefaultsAsync(new NetworkConfiguration(), lookUpPublicIp: false, CancellationToken.None);

        Assert.Null(defaults.Domain);
    }

    public void Dispose() => _listener.Stop();

    private static Task<ExposureDefaults> DefaultsFor(params string[] published) =>
        ExposureChecker.DefaultsAsync(new NetworkConfiguration { PublishedServerUriBySubnet = published }, lookUpPublicIp: false, CancellationToken.None);

    // Answers as Jellyfin at one path, with something that isn't HTTP at another, and "not found" anywhere else.
    // A connection closed without any answer would be sent again by HttpClient, so that is no use for one failed try.
    private int Serve(string? jellyfinAt, string? brokenAt = null)
    {
        _listener.Start();
        _ = AnswerAsync(jellyfinAt, brokenAt);
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private async Task AnswerAsync(string? jellyfinAt, string? brokenAt)
    {
        while (true)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                int read = await stream.ReadAsync(buffer);
                string path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ') is [_, var target, ..] ? target : string.Empty;
                _asked.Enqueue(path);
                string answer = path == jellyfinAt
                    ? $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {PublicInfo.Length}\r\nConnection: close\r\n\r\n{PublicInfo}"
                    : path == brokenAt
                        ? "Not an HTTP answer\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return; // the test is over
            }
        }
    }
}

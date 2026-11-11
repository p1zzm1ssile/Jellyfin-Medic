using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

// Transmission and Deluge have no list of file names to exclude, so the guard reads each torrent's files and
// marks the unsafe ones as not to be downloaded.
[Collection(PluginInstanceCollection.Name)]
public sealed class TorrentGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;
    private readonly Plugin _plugin;

    public TorrentGuardTests()
    {
        var paths = new TestPaths(_root);
        _plugin = new Plugin(paths, new DefaultSettingsSerializer());
        _plugin.Configuration.TorrentClientKind = "transmission";
        _plugin.Configuration.TorrentClientUrl = "http://transmission:9091";
        _plugin.Configuration.TorrentClientUser = "admin";
        _plugin.Configuration.TorrentBlock = true;
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetTorrentPassword("hunter2");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("http://host:9091", "http://host:9091/transmission/rpc")]
    [InlineData("http://host:9091/", "http://host:9091/transmission/rpc")]
    [InlineData("http://host:9091/transmission/web/", "http://host:9091/transmission/rpc")]
    [InlineData("https://example.org/torrents/transmission/web", "https://example.org/torrents/transmission/rpc")]
    [InlineData("https://example.org/torrents", "https://example.org/torrents/transmission/rpc")]
    public void Transmission_s_rpc_is_found_from_the_web_page_s_address(string saved, string rpc)
    {
        Assert.Equal(rpc, TransmissionRpc.RpcAddress(saved).ToString());
    }

    [Fact]
    public async Task Transmission_skips_program_files_after_its_session_handshake()
    {
        var fake = new FakeTransmission(
            [
                Torrent(1, "Film.2024.1080p", ["Film.2024.1080p/Film.mkv", "Film.2024.1080p/Setup.EXE", "Film.2024.1080p/Subs.rar"], [true, true, true]),
                // Skipped already: nothing to send.
                Torrent(2, "Series.S01", ["Series.S01/E01.mkv", "Series.S01/Codec.exe"], [true, false])
            ]);

        var state = await Guard(fake).SweepAsync(CancellationToken.None);

        var set = Assert.Single(fake.Calls, c => c.Method == "torrent-set");
        Assert.Multiple(
            () => Assert.Equal("[1]", set.Arguments.GetProperty("ids").GetRawText()),
            () => Assert.Equal("[1]", set.Arguments.GetProperty("files-unwanted").GetRawText()),
            () => Assert.Equal(1, fake.Conflicts),
            () => Assert.All(fake.Calls, c => Assert.Equal("session-1", c.Session)),
            () => Assert.All(fake.Calls, c => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:hunter2")), c.Authorization)),
            () => Assert.True(state.Reachable),
            () => Assert.Equal(2, state.Torrents),
            () => Assert.Equal(1, state.Skipped),
            () => Assert.Equal(["Setup.EXE in Film.2024.1080p"], state.Recent));
    }

    [Fact]
    public async Task Archives_are_skipped_only_when_ticked()
    {
        _plugin.Configuration.TorrentArchives = true;
        var fake = new FakeTransmission([Torrent(5, "Film", ["Film/Film.mkv", "Film/Film.part1.rar", "Film/Film.zip"], [true, true, true])]);

        await Guard(fake).SweepAsync(CancellationToken.None);

        var set = Assert.Single(fake.Calls, c => c.Method == "torrent-set");
        Assert.Equal("[1,2]", set.Arguments.GetProperty("files-unwanted").GetRawText());
    }

    [Fact]
    public async Task With_blocking_off_nothing_is_asked_until_check_now_and_nothing_is_skipped()
    {
        _plugin.Configuration.TorrentBlock = false;
        var fake = new FakeTransmission([Torrent(1, "Film", ["Film/Setup.exe"], [true])]);
        var guard = Guard(fake);

        await guard.SweepAsync(CancellationToken.None);
        Assert.Empty(fake.Calls);

        var state = await guard.SweepAsync(CancellationToken.None, asked: true);
        Assert.DoesNotContain(fake.Calls, c => c.Method == "torrent-set");
        Assert.Contains("blocking is off", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_password_is_not_tried_each_minute_but_check_now_tries_it()
    {
        var fake = new FakeTransmission([]) { Refuse = true };
        var guard = Guard(fake);

        var state = await guard.SweepAsync(CancellationToken.None);
        int asked = fake.Calls.Count;
        await guard.SweepAsync(CancellationToken.None);

        Assert.Contains("didn't accept the user name and password", state.Message, StringComparison.Ordinal);
        Assert.False(state.Reachable);
        Assert.Equal(asked, fake.Calls.Count);

        await guard.SweepAsync(CancellationToken.None, asked: true);
        Assert.True(fake.Calls.Count > asked);
    }

    [Fact]
    public async Task A_new_password_is_tried_at_once_after_a_refusal()
    {
        var fake = new FakeTransmission([]) { Refuse = true };
        var guard = Guard(fake);
        await guard.SweepAsync(CancellationToken.None);
        int asked = fake.Calls.Count;

        _store.SetTorrentPassword("correct horse");
        fake.Refuse = false;
        var state = await guard.SweepAsync(CancellationToken.None);

        Assert.True(fake.Calls.Count > asked);
        Assert.True(state.Reachable);
    }

    [Fact]
    public async Task Deluge_signs_in_connects_its_web_ui_and_sets_the_file_priorities()
    {
        _plugin.Configuration.TorrentClientKind = "deluge";
        _plugin.Configuration.TorrentClientUrl = "http://deluge:8112";
        var fake = new FakeDeluge();

        var state = await Guard(fake).SweepAsync(CancellationToken.None);

        Assert.Multiple(
            () => Assert.Equal(["auth.login", "web.connected", "web.get_hosts", "web.connect", "core.get_torrents_status", "core.set_torrent_options"], fake.Calls.Select(c => c.Method)),
            () => Assert.Equal("[\"hunter2\"]", fake.Calls[0].Params),
            () => Assert.Equal("[\"host-1\"]", fake.Calls[3].Params),
            () => Assert.Equal("[[\"abc123\"],{\"file_priorities\":[1,0,4]}]", fake.Calls[5].Params),
            () => Assert.Equal(1, state.Skipped));
    }

    [Fact]
    public async Task Deluge_turning_the_password_down_says_so()
    {
        _plugin.Configuration.TorrentClientKind = "deluge";
        _plugin.Configuration.TorrentClientUrl = "http://deluge:8112";
        var fake = new FakeDeluge { Refuse = true };

        var state = await Guard(fake).SweepAsync(CancellationToken.None);

        Assert.Equal(["auth.login"], fake.Calls.Select(c => c.Method));
        Assert.Contains("Deluge didn't accept the Web UI password", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_asked_until_a_client_is_chosen()
    {
        _plugin.Configuration.TorrentClientKind = string.Empty;
        var fake = new FakeTransmission([]);

        var state = await Guard(fake).SweepAsync(CancellationToken.None, asked: true);

        Assert.Empty(fake.Calls);
        Assert.False(state.Configured);
    }

    private TorrentGuard Guard(HttpMessageHandler handler) =>
        new(_store, NullLogger<TorrentGuard>.Instance, (kind, url, user, password) => kind == "deluge"
            ? new DelugeRpc(url, password, () => new HttpClient(handler, disposeHandler: false))
            : new TransmissionRpc(url, user, password, () => new HttpClient(handler, disposeHandler: false)));

    private static object Torrent(int id, string name, string[] files, bool[] wanted) =>
        new { id, name, files = files.Select(f => new { name = f, length = 100, bytesCompleted = 0 }).ToArray(), wanted };

    private sealed record Call(string Method, JsonElement Arguments, string? Session, string? Authorization);

    // A pretend transmission-daemon: 409 with a session id until it's sent, then torrent-get and torrent-set.
    private sealed class FakeTransmission(object[] torrents) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = new();

        public int Conflicts { get; private set; }

        public bool Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/transmission/rpc", request.RequestUri!.AbsolutePath);
            if (Refuse)
            {
                Calls.Add(new Call("refused", default, null, null));
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            string? session = request.Headers.TryGetValues("X-Transmission-Session-Id", out var s) ? s.Single() : null;
            if (session != "session-1")
            {
                Conflicts++;
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.Add("X-Transmission-Session-Id", "session-1");
                return conflict;
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string method = body.RootElement.GetProperty("method").GetString()!;
            Calls.Add(new Call(method, body.RootElement.GetProperty("arguments").Clone(), session, request.Headers.Authorization?.ToString()));
            object answer = method == "torrent-get"
                ? new { result = "success", arguments = new { torrents } }
                : new { result = "success", arguments = new { } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") };
        }
    }

    private sealed record DelugeCall(string Method, string Params);

    // A pretend Deluge Web UI with one daemon it isn't connected to yet, and one torrent of three files.
    private sealed class FakeDeluge : HttpMessageHandler
    {
        public List<DelugeCall> Calls { get; } = new();

        public bool Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/json", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string method = body.RootElement.GetProperty("method").GetString()!;
            Calls.Add(new DelugeCall(method, body.RootElement.GetProperty("params").GetRawText()));
            object? result = method switch
            {
                "auth.login" => !Refuse,
                "web.connected" => false,
                "web.get_hosts" => new object[] { new object[] { "host-1", "127.0.0.1", 58846, "localclient" } },
                "web.connect" => Array.Empty<object>(),
                "core.get_torrents_status" => new Dictionary<string, object>
                {
                    ["abc123"] = new
                    {
                        name = "Film.2024",
                        files = new[] { new { index = 0, path = "Film.2024/Film.mkv", size = 100 }, new { index = 1, path = "Film.2024/Install.exe", size = 1 }, new { index = 2, path = "Film.2024/Film.nfo", size = 1 } },
                        // The .nfo was set to high priority by hand, and keeps it.
                        file_priorities = new[] { 1, 1, 4 }
                    }
                },
                _ => null
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { result, error = (object?)null, id = 1 }), Encoding.UTF8, "application/json")
            };
            return response;
        }
    }
}

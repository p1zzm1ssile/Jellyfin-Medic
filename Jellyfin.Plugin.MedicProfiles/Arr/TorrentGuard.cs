using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Medic.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>What the Transmission or Deluge guard did last, for the Settings tab.</summary>
public sealed class TorrentGuardState
{
    public string Kind { get; set; } = string.Empty;

    public bool Configured { get; set; }

    public bool On { get; set; }

    public bool Reachable { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime? CheckedUtc { get; set; }

    public int Torrents { get; set; }

    // Files skipped since Jellyfin started, and the latest few by name.
    public int Skipped { get; set; }

    public List<string> Recent { get; set; } = new();
}

/// <summary>A torrent's files as the client lists them: the index the client knows each by, its path, and whether it's wanted.</summary>
public sealed record TorrentFiles(string Id, string Name, IReadOnlyList<(int Index, string Path, bool Wanted)> Files)
{
    // Deluge sets every file's priority at once, so it keeps the ones it read.
    public IReadOnlyList<int> Priorities { get; init; } = Array.Empty<int>();
}

/// <summary>Transmission's or Deluge's remote control, as far as the guard needs it.</summary>
public interface ITorrentRpc : IDisposable
{
    Task<List<TorrentFiles>> ListAsync(CancellationToken ct);

    Task SkipAsync(TorrentFiles torrent, IReadOnlyList<int> indexes, CancellationToken ct);
}

/// <summary>The client turned the user name and password down.</summary>
public sealed class TorrentRefusedException(string message) : Exception(message);

/// <summary>
/// Stops Transmission or Deluge downloading program files (.exe and friends) inside torrents. Neither has a list
/// of file names to exclude, as qBittorrent does, so once a minute the guard reads each torrent's files and marks
/// the unsafe ones as not to be downloaded (unwanted in Transmission, priority 0 in Deluge). A file already on disk
/// stays there; the guard only stops the rest of it arriving. Archives (.rar, .zip, .7z) are optional, as for qBittorrent.
/// </summary>
public sealed class TorrentGuard : IHostedService, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    // After a refused sign-in it waits this long, unless the settings change, so a wrong password isn't tried each minute.
    private static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(30);

    private readonly ArrStore _store;
    private readonly ILogger<TorrentGuard> _logger;
    private readonly Func<string, string, string, string, ITorrentRpc> _rpc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TorrentGuardState _state = new();
    private Timer? _timer;
    private int _refusedFor;
    private DateTime _refusedUntil;

    public TorrentGuard(ArrStore store, ILogger<TorrentGuard> logger)
        : this(store, logger, (kind, url, user, password) => kind == "deluge" ? new DelugeRpc(url, password) : new TransmissionRpc(url, user, password))
    {
    }

    // The client, for tests.
    internal TorrentGuard(ArrStore store, ILogger<TorrentGuard> logger, Func<string, string, string, string, ITorrentRpc> rpc)
    {
        _store = store;
        _logger = logger;
        _rpc = rpc;
    }

    public static string Name(string kind) => kind == "deluge" ? "Deluge" : "Transmission";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = SweepQuietlyAsync(), null, TimeSpan.FromSeconds(30), Every);
        return Task.CompletedTask;
    }

    private async Task SweepQuietlyAsync()
    {
        try
        {
            await SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: the torrent guard's check failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }

    public TorrentGuardState State()
    {
        lock (_state)
        {
            var (kind, url, _, _) = Settings();
            return new TorrentGuardState
            {
                Kind = kind,
                Configured = kind.Length > 0 && url.Length > 0,
                On = Plugin.Instance?.Configuration.TorrentBlock == true,
                Reachable = _state.Reachable,
                Message = _state.Message,
                CheckedUtc = _state.CheckedUtc,
                Torrents = _state.Torrents,
                Skipped = _state.Skipped,
                Recent = _state.Recent.ToList()
            };
        }
    }

    /// <summary>Looks through every torrent once, skipping unsafe files. Check now on the page runs this too, even after a refusal.</summary>
    public async Task<TorrentGuardState> SweepAsync(CancellationToken ct, bool asked = false)
    {
        var config = Plugin.Instance?.Configuration;
        var (kind, url, user, password) = Settings();
        if (config is null || kind.Length == 0 || url.Length == 0)
        {
            Set(false, "Choose Transmission or Deluge and add its address to use this.");
            return State();
        }

        if (!config.TorrentBlock && !asked)
        {
            return State();
        }

        int who = HashCode.Combine(kind, url, user, password);
        if (!asked && who == _refusedFor && DateTime.UtcNow < _refusedUntil)
        {
            return State();
        }

        // The minute's check skips a turn while another runs; Check now waits for it, so it reports a check of its own.
        if (!await _gate.WaitAsync(asked ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            return State();
        }

        try
        {
            using var rpc = _rpc(kind, url, user, password);
            var torrents = await rpc.ListAsync(ct).ConfigureAwait(false);
            var unsafeFiles = Patterns(config.TorrentArchives);
            int skippedNow = 0;
            var names = new List<string>();
            if (config.TorrentBlock)
            {
                foreach (var t in torrents)
                {
                    var skip = t.Files.Where(f => f.Wanted && IsUnsafe(f.Path, unsafeFiles)).ToList();
                    if (skip.Count == 0)
                    {
                        continue;
                    }

                    await rpc.SkipAsync(t, skip.Select(f => f.Index).ToList(), ct).ConfigureAwait(false);
                    skippedNow += skip.Count;
                    names.AddRange(skip.Select(f => Path.GetFileName(f.Path) + " in " + t.Name));
                    _logger.LogInformation("Medic Profiles: {Client} won't download {Count} unsafe file(s) in {Torrent}", Name(kind), skip.Count, t.Name);
                }
            }

            _refusedFor = 0;
            lock (_state)
            {
                _state.Reachable = true;
                _state.CheckedUtc = DateTime.UtcNow;
                _state.Torrents = torrents.Count;
                _state.Skipped += skippedNow;
                _state.Recent = names.Concat(_state.Recent).Take(10).ToList();
                _state.Message = config.TorrentBlock
                    ? $"{Name(kind)} answered. Medic Profiles checks its torrents every minute and skips " + (config.TorrentArchives ? "program files and archives." : "program files.")
                    : $"{Name(kind)} answered, but blocking is off, so nothing is skipped.";
            }
        }
        catch (TorrentRefusedException ex)
        {
            _refusedFor = who;
            _refusedUntil = DateTime.UtcNow + RefusedPause;
            Set(false, ex.Message + " Medic Profiles tries again in 30 minutes, or straight away when you save a new password or press Check now.");
        }
        catch (RedirectedException ex)
        {
            Set(false, $"{url} sent the request on to {ex.Target}. Medic Profiles doesn't follow that, so the password only goes to the address saved. Enter the address {Name(kind)} answers at directly.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("Medic Profiles: couldn't reach {Client}: {Message}", Name(kind), ex.Message);
            Set(false, $"Couldn't reach {Name(kind)} at {url} from the Jellyfin server.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Runs on a timer, so nothing may escape: an answer of the wrong shape (a proxy's page, a much older
            // version) is reported, not thrown.
            _logger.LogWarning(ex, "Medic Profiles: {Client} answered in a way Medic Profiles doesn't understand", Name(kind));
            Set(false, $"{Name(kind)} at {url} answered, but not in a way Medic Profiles understands. Check the address, and that it's {Name(kind)}'s Web UI." + (ex is InvalidDataException ? " (" + ex.Message + ")" : string.Empty));
        }
        finally
        {
            _gate.Release();
        }

        return State();
    }

    private void Set(bool reachable, string message)
    {
        lock (_state)
        {
            _state.Reachable = reachable;
            _state.Message = message;
            _state.CheckedUtc = DateTime.UtcNow;
        }
    }

    private (string Kind, string Url, string User, string Password) Settings()
    {
        var c = Plugin.Instance?.Configuration;
        string kind = c?.TorrentClientKind is "transmission" or "deluge" ? c.TorrentClientKind : string.Empty;
        return (kind, (c?.TorrentClientUrl ?? string.Empty).Trim().TrimEnd('/'), c?.TorrentClientUser ?? string.Empty, _store.GetTorrentPassword() ?? string.Empty);
    }

    // The extensions of QbitFileFilter's patterns ("*.exe" is ".exe"), so both clients skip the same files.
    internal static HashSet<string> Patterns(bool archives) =>
        new((archives ? QbitFileFilter.Programs.Concat(QbitFileFilter.Archives) : QbitFileFilter.Programs).Select(p => p.TrimStart('*')), StringComparer.OrdinalIgnoreCase);

    internal static bool IsUnsafe(string path, HashSet<string> extensions) => extensions.Contains(Path.GetExtension(path));
}

/// <summary>
/// Transmission's RPC (transmission-daemon 2.40 and later): the session id it hands out with a 409, then
/// torrent-get for the files and torrent-set with files-unwanted.
/// </summary>
public sealed class TransmissionRpc : ITorrentRpc
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private readonly Uri _rpc;
    private readonly string _user;
    private readonly string _password;
    private readonly Func<HttpClient> _client;
    private string? _session;

    public TransmissionRpc(string url, string user, string password, Func<HttpClient>? client = null)
    {
        _rpc = RpcAddress(url);
        _user = user;
        _password = password;
        _client = client ?? (() => new HttpClient(ServiceHttp.NoRedirects(), disposeHandler: true));
    }

    // The address people know is the web page's (http://host:9091 or .../transmission/web/); the RPC sits beside it.
    internal static Uri RpcAddress(string url)
    {
        var u = new Uri(url.Trim().TrimEnd('/') + "/");
        string path = u.AbsolutePath;
        int at = path.IndexOf("/transmission/", StringComparison.OrdinalIgnoreCase);
        return new Uri(u, (at >= 0 ? path[..at] : path.TrimEnd('/')) + "/transmission/rpc");
    }

    public async Task<List<TorrentFiles>> ListAsync(CancellationToken ct)
    {
        using var doc = await CallAsync(new { method = "torrent-get", arguments = new { fields = new[] { "id", "name", "files", "wanted" } } }, ct).ConfigureAwait(false);
        var list = new List<TorrentFiles>();
        if (!doc.RootElement.TryGetProperty("arguments", out var args) || !args.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Transmission's answer had no torrents list");
        }

        foreach (var t in torrents.EnumerateArray())
        {
            var files = t.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToList() : new List<JsonElement>();
            var wanted = t.TryGetProperty("wanted", out var w) && w.ValueKind == JsonValueKind.Array ? w.EnumerateArray().ToList() : new List<JsonElement>();
            list.Add(new TorrentFiles(
                t.TryGetProperty("id", out var id) ? id.ToString() : string.Empty,
                Json.Text(t, "name") ?? "A torrent",
                files.Select((file, i) => (i, Json.Text(file, "name") ?? string.Empty, i >= wanted.Count || wanted[i].ValueKind == JsonValueKind.True || (wanted[i].ValueKind == JsonValueKind.Number && wanted[i].GetInt32() != 0))).ToList()));
        }

        return list;
    }

    public async Task SkipAsync(TorrentFiles torrent, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        using var doc = await CallAsync(new Dictionary<string, object>
        {
            ["method"] = "torrent-set",
            ["arguments"] = new Dictionary<string, object> { ["ids"] = new[] { int.Parse(torrent.Id, System.Globalization.CultureInfo.InvariantCulture) }, ["files-unwanted"] = indexes }
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Each call has a client of its own.
    }

    private async Task<JsonDocument> CallAsync(object body, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(body);
        using var client = _client();
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _rpc) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (_user.Length > 0 || _password.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_user + ":" + _password)));
            }

            if (_session is not null)
            {
                request.Headers.Add("X-Transmission-Session-Id", _session);
            }

            using var response = await ServiceHttp.SendAsync(client, request, Limit, "Transmission", ct).ConfigureAwait(false);
            // Transmission hands out the session id with a 409 to the first call, and again when it changes.
            if (response.StatusCode == HttpStatusCode.Conflict && attempt == 0 && response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
            {
                _session = ids.FirstOrDefault();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TorrentRefusedException("Transmission didn't accept the user name and password.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new TorrentRefusedException("Transmission turned this server away. Add the Jellyfin server's address to its RPC whitelist, or switch the whitelist off.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Transmission answered with {(int)response.StatusCode}");
            }

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (Json.Text(doc.RootElement, "result") is { } result && result != "success")
            {
                doc.Dispose();
                throw new InvalidDataException("Transmission says: " + result);
            }

            return doc;
        }
    }
}

/// <summary>
/// Deluge's Web UI JSON-RPC (Deluge 2): auth.login with the Web UI password, connecting the Web UI to its first
/// daemon when it isn't, then core.get_torrents_status for the files and core.set_torrent_options for their priorities.
/// </summary>
public sealed class DelugeRpc : ITorrentRpc
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private readonly Uri _json;
    private readonly string _password;
    private readonly Func<HttpClient> _client;
    private HttpClient? _session;
    private int _id;

    public DelugeRpc(string url, string password, Func<HttpClient>? client = null)
    {
        _json = new Uri(url.Trim().TrimEnd('/') + "/json");
        _password = password;
        _client = client ?? (() =>
        {
            var handler = ServiceHttp.NoRedirects();
            handler.CookieContainer = new CookieContainer();
            handler.UseCookies = true;
            return new HttpClient(handler, disposeHandler: true);
        });
    }

    public async Task<List<TorrentFiles>> ListAsync(CancellationToken ct)
    {
        await SignInAsync(ct).ConfigureAwait(false);
        using var doc = await CallAsync("core.get_torrents_status", new object[] { new Dictionary<string, object>(), new[] { "name", "files", "file_priorities" } }, ct).ConfigureAwait(false);
        var result = doc.RootElement.GetProperty("result");
        var list = new List<TorrentFiles>();
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Deluge's answer had no torrents");
        }

        foreach (var t in result.EnumerateObject())
        {
            var priorities = t.Value.TryGetProperty("file_priorities", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.TryGetInt32(out int n) ? n : 1).ToList()
                : new List<int>();
            var files = t.Value.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(file =>
                {
                    int index = Json.Int(file, "index") ?? 0;
                    return (index, Json.Text(file, "path") ?? string.Empty, index >= priorities.Count || priorities[index] != 0);
                }).ToList()
                : new List<(int, string, bool)>();
            list.Add(new TorrentFiles(t.Name, Json.Text(t.Value, "name") ?? "A torrent", files) { Priorities = priorities });
        }

        return list;
    }

    public async Task SkipAsync(TorrentFiles torrent, IReadOnlyList<int> indexes, CancellationToken ct)
    {
        var priorities = torrent.Priorities.Count > 0 ? torrent.Priorities.ToArray() : Enumerable.Repeat(1, torrent.Files.Count).ToArray();
        foreach (int i in indexes.Where(i => i >= 0 && i < priorities.Length))
        {
            priorities[i] = 0;
        }

        using var doc = await CallAsync("core.set_torrent_options", new object[] { new[] { torrent.Id }, new Dictionary<string, object> { ["file_priorities"] = priorities } }, ct).ConfigureAwait(false);
    }

    public void Dispose() => _session?.Dispose();

    private async Task SignInAsync(CancellationToken ct)
    {
        _session?.Dispose();
        _session = _client();
        using (var login = await CallAsync("auth.login", new object[] { _password }, ct).ConfigureAwait(false))
        {
            if (login.RootElement.GetProperty("result").ValueKind != JsonValueKind.True)
            {
                throw new TorrentRefusedException("Deluge didn't accept the Web UI password.");
            }
        }

        using var connected = await CallAsync("web.connected", Array.Empty<object>(), ct).ConfigureAwait(false);
        if (connected.RootElement.GetProperty("result").ValueKind == JsonValueKind.True)
        {
            return;
        }

        // The Web UI isn't connected to a daemon yet: connect it to the first one it knows, as its Connection Manager would.
        using var hosts = await CallAsync("web.get_hosts", Array.Empty<object>(), ct).ConfigureAwait(false);
        var first = hosts.RootElement.GetProperty("result").EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Deluge's Web UI has no daemon to connect to");
        }

        using var _ = await CallAsync("web.connect", new object[] { first[0].GetString() ?? string.Empty }, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> CallAsync(string method, object[] args, CancellationToken ct)
    {
        _session ??= _client();
        using var request = new HttpRequestMessage(HttpMethod.Post, _json)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { method, @params = args, id = ++_id }), Encoding.UTF8, "application/json")
        };
        using var response = await ServiceHttp.SendAsync(_session, request, Limit, "Deluge", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Deluge answered with {(int)response.StatusCode}");
        }

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            string message = Json.Text(error, "message") ?? "an error";
            doc.Dispose();
            // Deluge answers "Not authenticated" (code 1) to anything before a sign-in, or after it has expired.
            if (Json.Int(error, "code") == 1)
            {
                throw new TorrentRefusedException("Deluge didn't accept the Web UI password.");
            }

            throw new InvalidDataException("Deluge says: " + message);
        }

        return doc;
    }
}

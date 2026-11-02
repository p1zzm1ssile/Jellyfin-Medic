using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Medic.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>What qBittorrent's "Excluded file names" list holds, as far as Medic Profiles is concerned.</summary>
public sealed class FileFilterState
{
    public bool Configured { get; set; }

    public bool Reachable { get; set; }

    public string Message { get; set; } = string.Empty;

    // qBittorrent turned the sign-in down, so Medic Profiles waits for the password to change or for Try again.
    public bool Refused { get; set; }

    public bool ListOn { get; set; }

    // Which of Medic Profiles' patterns are in the list.
    public bool BlocksPrograms { get; set; }

    public bool BlocksArchives { get; set; }

    public List<string> Patterns { get; set; } = new();
}

/// <summary>
/// Stops qBittorrent downloading program files (.exe and friends) inside films and series, using its
/// "Excluded file names" list (qBittorrent 4.6 and later). The files are skipped as the torrent starts,
/// so they never land on disk. Sonarr and Radarr can't do this: they only see release names, not the
/// files inside. Archives (.rar, .zip, .7z) are optional, because many genuine releases come packed and
/// Sonarr and Radarr unpack them.
/// </summary>
public class QbitFileFilter
{
    public static readonly string[] Programs =
    {
        "*.exe", "*.msi", "*.bat", "*.cmd", "*.com", "*.scr", "*.pif", "*.lnk", "*.vbs", "*.vbe", "*.ps1", "*.jar", "*.apk", "*.dll"
    };

    public static readonly string[] Archives = { "*.rar", "*.zip", "*.7z" };

    private readonly ArrStore _store;
    private readonly ILogger<QbitFileFilter> _logger;

    // qBittorrent needs its own client to keep the sign-in cookie, so the shared HttpClient factory isn't used.
    public QbitFileFilter(ArrStore store, ILogger<QbitFileFilter> logger)
    {
        _store = store;
        _logger = logger;
    }

    private static string? BaseUrl()
    {
        string? url = Plugin.Instance?.Configuration.QbitUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }

    public Task<FileFilterState> StateAsync(CancellationToken ct) => StateAsync(retry: false, ct);

    /// <summary>
    /// The state, signing in once even after a refusal: for the owner who fixed the password in qBittorrent,
    /// or waited out its ban, since Medic Profiles can't tell that on its own.
    /// </summary>
    public Task<FileFilterState> RetryAsync(CancellationToken ct) => StateAsync(retry: true, ct);

    private async Task<FileFilterState> StateAsync(bool retry, CancellationToken ct)
    {
        var state = new FileFilterState { Configured = BaseUrl() is not null };
        if (!state.Configured)
        {
            state.Message = "Add qBittorrent's address to use this.";
            return state;
        }

        var (prefs, error, refused) = await WithSessionAsync(client => GetPrefsAsync(client, ct), retry, ct).ConfigureAwait(false);
        if (prefs is null)
        {
            state.Refused = refused;
            state.Message = error.Length > 0 ? error : "qBittorrent answered, but didn't return its settings.";
            return state;
        }

        state.Reachable = true;
        Fill(state, prefs.Value);
        state.Message = !prefs.Value.TryGetProperty("excluded_file_names", out _)
            ? "This qBittorrent is too old to exclude file names. Update to 4.6 or later."
            : state.ListOn && state.BlocksPrograms ? "qBittorrent skips program files in new downloads." : "Program files aren't blocked yet.";
        return state;
    }

    /// <summary>
    /// Adds (or, with block false, removes) Medic Profiles' patterns. Only patterns it added itself are ever
    /// removed, so entries the owner had (even the same ones) stay, and unblocking switches the list back
    /// on or off as it was before.
    /// </summary>
    public async Task<(bool Ok, string Message, bool Refused)> ApplyAsync(bool block, bool archives, CancellationToken ct)
    {
        var (result, error, refused) = await WithSessionAsync(client => ApplyWithAsync(client, block, archives, ct), retry: false, ct).ConfigureAwait(false);
        return result is { } done ? (done.Ok, done.Message, false) : (false, error, refused);
    }

    private async Task<(bool Ok, string Message)?> ApplyWithAsync(HttpClient client, bool block, bool archives, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var prefs = await GetPrefsAsync(client, ct).ConfigureAwait(false);
        if (prefs is null)
        {
            return (false, "qBittorrent answered, but didn't return its settings.");
        }

        if (!prefs.Value.TryGetProperty("excluded_file_names", out var current))
        {
            return (false, "This qBittorrent can't exclude file names. Update it to 4.6 or later.");
        }

        var list = Split(current.GetString());
        bool wasOn = prefs.Value.TryGetProperty("excluded_file_names_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        // A record made on another qBittorrent says nothing about this one.
        string host = Api.ProfilesController.Authority(BaseUrl() ?? string.Empty);
        bool sameServer = config.QbitPatternsHost.Length == 0 || string.Equals(config.QbitPatternsHost, host, StringComparison.OrdinalIgnoreCase);
        var added = new HashSet<string>(sameServer ? config.QbitPatternsAdded ?? Array.Empty<string>() : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        bool? recordedWasOn = sameServer ? config.QbitListWasOn : null;
        if (config.QbitPatternsHost.Length == 0 && config.QbitListWasOn is null && added.Count == 0)
        {
            // Medic Profiles 1.1.0 and 1.1.1 didn't record what they added: their full set counts as theirs.
            var present = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            if (Programs.All(present.Contains))
            {
                added.UnionWith(Programs);
                if (Archives.All(present.Contains))
                {
                    added.UnionWith(Archives);
                }
            }
        }

        var wanted = new HashSet<string>(block ? (archives ? Programs.Concat(Archives) : Programs) : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        list = list.Where(p => !added.Contains(p) || wanted.Contains(p)).ToList();
        added.IntersectWith(wanted);
        foreach (var pattern in wanted.Where(p => !list.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            list.Add(pattern);
            added.Add(pattern);
        }

        bool listWasOn = recordedWasOn ?? wasOn;
        bool on = block || (listWasOn && list.Count > 0);
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["excluded_file_names_enabled"] = on,
            ["excluded_file_names"] = string.Join('\n', list)
        });
        using var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("json", json) });
        using var response = await client.PostAsync(BaseUrl() + "/api/v2/app/setPreferences", form, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SessionExpiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, $"qBittorrent refused the change (HTTP {(int)response.StatusCode}).");
        }

        config.QbitPatternsAdded = block ? added.ToArray() : Array.Empty<string>();
        config.QbitListWasOn = block ? listWasOn : null;
        config.QbitPatternsHost = host;
        Plugin.Instance.SaveConfiguration();

        return (true, block
            ? "Done. qBittorrent now skips " + (archives ? "program files and archives" : "program files") + " in every new download, not only films and series. Anything that was already in the list is kept."
            : "Done. Only the entries Medic Profiles added are gone, and the list is switched " + (on ? "on" : "off") + " as it was before.");
    }

    private static void Fill(FileFilterState state, JsonElement prefs)
    {
        state.ListOn = prefs.TryGetProperty("excluded_file_names_enabled", out var on) && on.ValueKind == JsonValueKind.True;
        state.Patterns = prefs.TryGetProperty("excluded_file_names", out var list) ? Split(list.GetString()) : new List<string>();
        var set = new HashSet<string>(state.Patterns, StringComparer.OrdinalIgnoreCase);
        state.BlocksPrograms = Programs.All(set.Contains);
        state.BlocksArchives = Archives.All(set.Contains);
    }

    private static List<string> Split(string? text) =>
        (text ?? string.Empty).Split('\n', '\r').Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The Web UI's preferences, or null when qBittorrent answers with something else.</summary>
    private static async Task<JsonElement?> GetPrefsAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(BaseUrl() + "/api/v2/app/preferences", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SessionExpiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- Signing in ----------

    // One signed-in session is kept and reused, so opening the page doesn't sign in each time: every failed
    // sign-in counts towards qBittorrent's ban on this server's address (5 by default). Saving the settings,
    // Test and the other buttons all reload this state, so a refusal can't be retried on its own: after the
    // first it waits 5 minutes, after the second it stops. New details are tried at once, and Try again
    // spends exactly one sign-in.
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(5);
    private const int RefusalsBeforeStopping = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient? _session;
    private string _sessionBase = string.Empty;
    private int _signedInAs; // the address, user name and password the session, or the refusals, are for
    private DateTime _sessionUntil;
    private string? _refusal;
    private int _refusals;
    private DateTime _refusedUntil;

    /// <summary>qBittorrent answered 403 to a call after signing in: the session has ended on its side.</summary>
    private sealed class SessionExpiredException : Exception
    {
    }

    /// <summary>
    /// Runs work with a signed-in session; if qBittorrent has dropped the session, signs in once more and runs it again.
    /// Returns the work's result, or default, the reason it couldn't run and whether qBittorrent refused the sign-in.
    /// With retry, a refused sign-in is tried once more.
    /// </summary>
    private async Task<(T? Result, string Error, bool Refused)> WithSessionAsync<T>(Func<HttpClient, Task<T>> work, bool retry, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var (client, error, refused) = await SessionAsync(retry && attempt == 0, ct).ConfigureAwait(false);
            if (client is null)
            {
                return (default, error, refused);
            }

            try
            {
                return (await work(client).ConfigureAwait(false), string.Empty, false);
            }
            catch (SessionExpiredException) when (attempt == 0)
            {
                await ForgetSessionAsync().ConfigureAwait(false);
            }
            catch (SessionExpiredException)
            {
                await ForgetSessionAsync().ConfigureAwait(false);
                return (default, "qBittorrent keeps ending Medic Profiles' session. Check the user name and password.", false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogInformation("Medic Profiles: couldn't reach qBittorrent: {Message}", ex.Message);
                await ForgetSessionAsync().ConfigureAwait(false);
                return (default, "Couldn't reach qBittorrent at that address from the Jellyfin server.", false);
            }
        }
    }

    private async Task<(HttpClient? Client, string Error, bool Refused)> SessionAsync(bool retry, CancellationToken ct)
    {
        string? baseUrl = BaseUrl();
        if (baseUrl is null)
        {
            return (null, "Add qBittorrent's address first.", false);
        }

        string user = Plugin.Instance?.Configuration.QbitUser ?? string.Empty;
        string password = _store.GetQbitPassword() ?? string.Empty;
        int who = HashCode.Combine(baseUrl, user, password);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (who == _signedInAs && _session is not null && now < _sessionUntil)
            {
                return (_session, string.Empty, false);
            }

            if (who != _signedInAs)
            {
                _refusals = 0;
                _refusal = null;
            }
            else if (_refusal is not null && !retry && (_refusals >= RefusalsBeforeStopping || now < _refusedUntil))
            {
                return (null, _refusal, true);
            }

            await SignOutLockedAsync().ConfigureAwait(false);
            _signedInAs = who;
            var (client, error, refused) = await SignInAsync(baseUrl, user, password, ct).ConfigureAwait(false);
            if (client is not null)
            {
                _session = client;
                _sessionBase = baseUrl;
                _sessionUntil = now + SessionLife;
                _refusals = 0;
                _refusal = null;
            }
            else if (refused)
            {
                _refusals++;
                _refusedUntil = now + RefusedPause;
                _refusal = error + (_refusals >= RefusalsBeforeStopping
                    ? " After two refusals Medic Profiles has stopped signing in, since qBittorrent blocks this server after 5 by default."
                    : " Medic Profiles won't try again for 5 minutes, so qBittorrent doesn't block this server.")
                    + " A new password saved here is tried at once. If you fixed it in qBittorrent, press Try qBittorrent again.";
                error = _refusal;
            }

            return (client, error, refused);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ForgetSessionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SignOutLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Signs the kept session out of qBittorrent (best effort) and drops it. Call with _gate held.
    private async Task SignOutLockedAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _session.PostAsync(_sessionBase + "/api/v2/auth/logout", null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // It expires on qBittorrent's side anyway.
        }

        _session.Dispose();
        _session = null;
    }

    /// <summary>
    /// Signs in to qBittorrent's Web UI. Refused is true when qBittorrent turned the user name and password
    /// down (or is blocking sign-ins), as opposed to not being reachable.
    /// </summary>
    private async Task<(HttpClient? Client, string Error, bool Refused)> SignInAsync(string baseUrl, string user, string password, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        // Redirects aren't followed: on a 307 or 308 the form, password and all, would be posted again to the new address.
        var handler = ServiceHttp.NoRedirects();
        handler.CookieContainer = cookies;
        handler.UseCookies = true;
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", user),
                new KeyValuePair<string, string>("password", password)
            });
            var asked = new Uri(baseUrl + "/api/v2/auth/login");
            using var response = await client.PostAsync(asked, form, ct).ConfigureAwait(false);
            if (ServiceHttp.RedirectTarget(response, asked) is { } target)
            {
                client.Dispose();
                _logger.LogWarning("Medic Profiles: qBittorrent at {Url} redirected the sign-in to {Target}", baseUrl, target);
                return (null, $"{baseUrl} sent the sign-in on to {target}. Medic Profiles doesn't follow that, so the password only goes to the address saved. Enter the address qBittorrent's Web UI answers at directly.", false);
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Up to qBittorrent 5.1 a sign-in answers 200 with "Ok." or "Fails.". From 5.2 it answers 204 with no
            // text when signed in and 401 for a wrong password. Both answer 403 while this address is banned.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                client.Dispose();
                return (null, "qBittorrent has blocked sign-ins from this server after too many wrong passwords. Its ban lasts an hour by default, or until qBittorrent restarts.", true);
            }

            if (!response.IsSuccessStatusCode || body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                client.Dispose();
                return (null, "qBittorrent didn't accept the user name and password.", true);
            }

            return (client, string.Empty, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("Medic Profiles: couldn't reach qBittorrent at {Url}: {Message}", baseUrl, ex.Message);
            client.Dispose();
            return (null, "Couldn't reach qBittorrent at that address from the Jellyfin server.", false);
        }
    }
}

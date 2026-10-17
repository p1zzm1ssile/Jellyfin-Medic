using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

/// <summary>
/// Every failed sign-in counts towards qBittorrent's ban on the Jellyfin server (5 by default), and the settings
/// page reloads the file filter state after every save, Test and block. So once qBittorrent refuses the sign-in,
/// only "Try qBittorrent again" or a new password may sign in again. Runs the real QbitFileFilter against a
/// pretend qBittorrent that counts sign-ins.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class QbitSignInPauseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;
    private readonly FakeQbit _qbit = new();
    private readonly QbitFileFilter _filter;

    public QbitSignInPauseTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.QbitUrl = _qbit.Url;
        plugin.Configuration.QbitUser = "admin";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetQbitPassword("wrong");
        _filter = new QbitFileFilter(_store, NullLogger<QbitFileFilter>.Instance);
    }

    public void Dispose()
    {
        _qbit.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_refused_sign_in_is_not_tried_again_by_page_reloads_or_block()
    {
        var first = await _filter.StateAsync(CancellationToken.None);
        Assert.True(first.Refused);
        Assert.False(first.Reachable);
        Assert.Equal(1, _qbit.SignIns);

        // A save, Test and the other buttons each reload the state; Block and Unblock go through ApplyAsync.
        for (int i = 0; i < 3; i++)
        {
            var again = await _filter.StateAsync(CancellationToken.None);
            Assert.True(again.Refused);
        }

        var (ok, message, refused) = await _filter.ApplyAsync(block: true, archives: false, CancellationToken.None);
        Assert.False(ok);
        Assert.True(refused);
        Assert.Contains("Try qBittorrent again", message, StringComparison.Ordinal);
        await _filter.ApplyAsync(block: false, archives: false, CancellationToken.None);

        Assert.Equal(1, _qbit.SignIns);
    }

    [Fact]
    public async Task Try_again_spends_exactly_one_sign_in_even_after_signing_in_has_stopped()
    {
        await _filter.StateAsync(CancellationToken.None);
        var retried = await _filter.RetryAsync(CancellationToken.None);
        Assert.True(retried.Refused);
        Assert.Equal(2, _qbit.SignIns);
        Assert.Contains("stopped signing in", retried.Message, StringComparison.Ordinal);

        // Two refusals: routine reloads stay away for good.
        await _filter.StateAsync(CancellationToken.None);
        await _filter.ApplyAsync(block: true, archives: false, CancellationToken.None);
        Assert.Equal(2, _qbit.SignIns);

        // The owner fixed the password in qBittorrent itself, so the saved one now works; Try again finds that out.
        _qbit.Password = "wrong";
        var fixedState = await _filter.RetryAsync(CancellationToken.None);
        Assert.Equal(3, _qbit.SignIns);
        Assert.False(fixedState.Refused);
        Assert.True(fixedState.Reachable);

        // The session is kept, so later reloads don't sign in again.
        await _filter.StateAsync(CancellationToken.None);
        Assert.Equal(3, _qbit.SignIns);
    }

    [Fact]
    public async Task A_new_password_is_tried_at_once()
    {
        await _filter.StateAsync(CancellationToken.None);
        await _filter.RetryAsync(CancellationToken.None);
        Assert.Equal(2, _qbit.SignIns);

        _store.SetQbitPassword("secret");
        var state = await _filter.StateAsync(CancellationToken.None);

        Assert.Equal(3, _qbit.SignIns);
        Assert.False(state.Refused);
        Assert.True(state.Reachable);
    }

    [Fact]
    public async Task A_ban_counts_as_a_refusal_and_is_not_tried_again_either()
    {
        _qbit.Banned = true;

        var state = await _filter.StateAsync(CancellationToken.None);
        await _filter.StateAsync(CancellationToken.None);
        await _filter.ApplyAsync(block: true, archives: false, CancellationToken.None);

        Assert.True(state.Refused);
        Assert.Contains("blocked sign-ins", state.Message, StringComparison.Ordinal);
        Assert.Equal(1, _qbit.SignIns);
    }

    /// <summary>
    /// A pretend qBittorrent Web UI on a local port, answering sign-ins the way qBittorrent 5.1 does
    /// ("Ok." or "Fails.", 403 while banned) and its preferences once signed in.
    /// </summary>
    private sealed class FakeQbit : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _signIns;

        public FakeQbit()
        {
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public string Password { get; set; } = "secret";

        public bool Banned { get; set; }

        public int SignIns => Volatile.Read(ref _signIns);

        public void Dispose() => _listener.Close();

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    await AnswerAsync(context).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            switch (request.Url!.AbsolutePath)
            {
                case "/api/v2/auth/login":
                    Interlocked.Increment(ref _signIns);
                    var form = System.Web.HttpUtility.ParseQueryString(body);
                    if (Banned)
                    {
                        response.StatusCode = 403;
                        await WriteAsync(response, "Your IP address has been banned after too many failed authentication attempts.").ConfigureAwait(false);
                    }
                    else if (form["username"] == "admin" && form["password"] == Password)
                    {
                        response.AppendHeader("Set-Cookie", "SID=good; HttpOnly; path=/");
                        await WriteAsync(response, "Ok.").ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteAsync(response, "Fails.").ConfigureAwait(false);
                    }

                    return;
                case "/api/v2/auth/logout":
                    return;
                case "/api/v2/app/preferences" when request.Headers["Cookie"]?.Contains("SID=good", StringComparison.Ordinal) == true:
                    response.ContentType = "application/json";
                    await WriteAsync(response, """{"excluded_file_names_enabled":false,"excluded_file_names":""}""").ConfigureAwait(false);
                    return;
                case "/api/v2/app/preferences":
                    response.StatusCode = 403;
                    return;
                default:
                    response.StatusCode = 404;
                    return;
            }
        }

        private static async Task WriteAsync(HttpListenerResponse response, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
    }
}

using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

[Collection(PluginInstanceCollection.Name)]
public sealed class ArrClientTests : IDisposable
{
    private const string SignInPage = "<!DOCTYPE html><html><body><form action=\"/login\">Sign in</form></body></html>";

    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;

    public ArrClientTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.SonarrUrl = "http://sonarr:8989";
        plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
        _store.SetKey(ArrApp.Radarr, "radarr-key");
    }

    [Theory]
    [InlineData(400, """{"message":"Not allowed"}""", "Sonarr says: Not allowed")]
    [InlineData(400, """[{"propertyName":"Path","errorMessage":"Path is wrong"}]""", "Sonarr says: Path is wrong")]
    [InlineData(400, """["Path is wrong"]""", "Sonarr answered with 400.")]
    [InlineData(500, "<html>Server error</html>", "Sonarr answered with 500.")]
    [InlineData(404, """{"message":"NotFound"}""", "Sonarr says that wasn't found. It may have been removed already.")]
    public async Task An_error_answer_is_worded_from_what_Sonarr_sent(int status, string body, string message)
    {
        // An answer of any shape is still an answer: never "couldn't reach Sonarr".
        var arr = Client(_ => Answer((HttpStatusCode)status, body));

        using var result = await arr.GetAsync(ArrApp.Sonarr, "queue", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(message, result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Not found</html>")]
    public async Task A_404_that_isnt_from_Sonarr_points_at_the_address(string body)
    {
        // Sonarr words its own 404s as JSON; an empty or HTML one means the address or URL base is wrong.
        var arr = Client(_ => Answer(HttpStatusCode.NotFound, body, "text/html"));

        using var result = await arr.DeleteAsync(ArrApp.Sonarr, "queue/7", null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.DoesNotContain("removed already", result.Message, StringComparison.Ordinal);
        Assert.Contains("URL base", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_passes_when_Sonarr_answers_its_status()
    {
        var arr = Client(_ => Answer(HttpStatusCode.OK, """{"appName":"Sonarr","instanceName":"Sonarr","version":"4.0.15"}"""));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Sonarr answered (version 4.0.15) and accepted the key.", message);
    }

    [Theory]
    [InlineData(SignInPage, "text/html")]
    [InlineData("""{"appName":"Radarr","version":"5.26.2"}""", "application/json")]
    [InlineData("[]", "application/json")]
    public async Task Test_fails_when_something_else_answers(string body, string type)
    {
        // A proxy's sign-in page answers 200 too, as does Radarr at Sonarr's address.
        var arr = Client(_ => Answer(HttpStatusCode.OK, body, type));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("http://sonarr:8989", message, StringComparison.Ordinal);
        Assert.Contains("URL base", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Test_fails_on_a_redirect_names_where_it_went_and_doesnt_follow_it(HttpStatusCode status)
    {
        var sent = new List<string>();
        var arr = Client(request =>
        {
            sent.Add(request.RequestUri!.ToString());
            return MovedTo(status, "http://auth.example/login?rd=sonarr");
        });

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("http://auth.example/login?rd=sonarr", message, StringComparison.Ordinal);
        Assert.Equal(new[] { "http://sonarr:8989/api/v3/system/status" }, sent);
    }

    [Fact]
    public async Task Test_fails_even_when_the_redirect_is_to_Sonarr_and_names_the_full_address()
    {
        // Sonarr with a URL base sends base-less calls on with a 307; the address to save is the one it points at.
        var arr = Client(_ => MovedTo(HttpStatusCode.TemporaryRedirect, "/sonarr/api/v3/system/status"));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("http://sonarr:8989/sonarr/api/v3/system/status", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirected_change_isnt_sent_on()
    {
        // A 307 would post the same body, key and all, to the new address.
        var sent = new List<string>();
        var arr = Client(request =>
        {
            sent.Add(request.Method + " " + request.RequestUri);
            return MovedTo(HttpStatusCode.TemporaryRedirect, "http://elsewhere.example/api/v3/command");
        });

        using var result = await arr.PostRawAsync(ArrApp.Sonarr, "command", "{}", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(new Uri("http://elsewhere.example/api/v3/command"), result.RedirectedTo);
        Assert.Contains("http://elsewhere.example/api/v3/command", result.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "POST http://sonarr:8989/api/v3/command" }, sent);
    }

    [Fact]
    public async Task An_answer_from_another_address_still_fails()
    {
        // Should a client follow the redirect itself, the call still fails and names where it went.
        var arr = Client(request => Redirected(request, "http://sonarr:8989/sonarr/api/v3/queue", request.Method, """{"records":[],"totalRecords":0}"""));

        using var result = await arr.GetAsync(ArrApp.Sonarr, "queue", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("http://sonarr:8989/sonarr/api/v3/queue", result.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ArrApp, int, string> RedirectsFromTheSavedAddress()
    {
        var data = new TheoryData<ArrApp, int, string>();
        foreach (var app in new[] { ArrApp.Sonarr, ArrApp.Radarr })
        {
            foreach (int status in new[] { 301, 302, 303, 307, 308 })
            {
                data.Add(app, status, "GET");
                data.Add(app, status, "POST");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RedirectsFromTheSavedAddress))]
    public async Task The_registered_client_never_sends_the_key_on_to_where_a_redirect_points(ArrApp app, int status, string method)
    {
        // Real servers and the client the plugin registers: .NET would send X-Api-Key, and on a 307 or 308 the body, on.
        using var target = new LocalServer(_ => (200, null, """{"appName":"Sonarr","version":"4.0.15"}"""));
        using var saved = new LocalServer(request => (status, target.Url + request.Url!.PathAndQuery, string.Empty));
        string key = app == ArrApp.Sonarr ? "sonarr-key" : "radarr-key";
        if (app == ArrApp.Sonarr)
        {
            Plugin.Instance!.Configuration.SonarrUrl = saved.Url;
        }
        else
        {
            Plugin.Instance!.Configuration.RadarrUrl = saved.Url;
        }

        using var services = Registered();
        var arr = services.GetRequiredService<ArrClient>();

        using var result = method == "GET"
            ? await arr.GetAsync(app, "queue", CancellationToken.None)
            : await arr.PostRawAsync(app, "command", """{"name":"RefreshMonitoredDownloads"}""", CancellationToken.None);

        Assert.Empty(target.Received);
        var sent = Assert.Single(saved.Received);
        Assert.Equal((method, key), (sent.Method, sent.ApiKey));
        Assert.False(result.Ok);
        Assert.Contains($"{saved.Url} sent the request on to {target.Url}/api/v3/", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registered_client_talks_to_Sonarr_as_Jellyfin_does()
    {
        // Jellyfin's default client, which these calls went through before, names the server and asks for JSON first.
        using var sonarr = new LocalServer(_ => (200, null, """{"appName":"Sonarr","version":"4.0.15"}"""));
        Plugin.Instance!.Configuration.SonarrUrl = sonarr.Url;
        using var services = Registered();

        var (ok, message) = await services.GetRequiredService<ArrClient>().StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.True(ok, message);
        var request = Assert.Single(sonarr.Received);
        Assert.Equal("Jellyfin-Server/12.2.0", request.UserAgent);
        Assert.Equal("application/json; q=1.0, application/xml; q=0.9, */*; q=0.8", request.Accept);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task The_qBittorrent_sign_in_doesnt_post_the_password_on_to_where_a_redirect_points(int status)
    {
        using var target = new LocalServer(_ => (200, null, "Ok."));
        using var saved = new LocalServer(request => (status, target.Url + request.Url!.PathAndQuery, string.Empty));
        Plugin.Instance!.Configuration.QbitUrl = saved.Url;
        Plugin.Instance.Configuration.QbitUser = "admin";
        _store.SetQbitPassword("hunter2");
        var qbit = new QbitFileFilter(_store, NullLogger<QbitFileFilter>.Instance);

        var state = await qbit.StateAsync(CancellationToken.None);

        Assert.Empty(target.Received);
        Assert.Contains("password=hunter2", Assert.Single(saved.Received).Body, StringComparison.Ordinal);
        Assert.False(state.Reachable);
        Assert.False(state.Refused);
        Assert.Contains($"{saved.Url} sent the sign-in on to {target.Url}/api/v2/auth/login", state.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInPage, "text/html")]
    [InlineData("[]", "application/json")]
    public async Task Import_fails_when_the_answer_isnt_a_command(string body, string type)
    {
        var arr = Client(request => request.Method == HttpMethod.Get
            ? Answer(HttpStatusCode.OK, """[{"path":"/downloads/Show.S01E01.mkv","relativePath":"Show.S01E01.mkv"}]""")
            : Answer(HttpStatusCode.OK, body, type));
        var downloads = new DownloadsService(arr, _store);
        var file = new ImportFileRequest { Path = "/downloads/Show.S01E01.mkv", MediaId = 3, EpisodeIds = [11] };

        var (ok, message) = await downloads.ImportAsync(ArrApp.Sonarr, "ABC", "move", [file], "Show", null, CancellationToken.None);

        Assert.False(ok);
        Assert.DoesNotContain("importing", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_passes_when_Sonarr_starts_the_command()
    {
        var arr = Client(request => request.Method == HttpMethod.Get
            ? Answer(HttpStatusCode.OK, """[{"path":"/downloads/Show.S01E01.mkv","relativePath":"Show.S01E01.mkv"}]""")
            : Answer(HttpStatusCode.Created, """{"id":42,"name":"ManualImport","status":"queued"}"""));
        var downloads = new DownloadsService(arr, _store);
        var file = new ImportFileRequest { Path = "/downloads/Show.S01E01.mkv", MediaId = 3, EpisodeIds = [11] };

        var (ok, message) = await downloads.ImportAsync(ArrApp.Sonarr, "ABC", "move", [file], "Show", null, CancellationToken.None);

        Assert.True(ok, message);
    }

    [Fact]
    public async Task A_slow_Sonarr_doesnt_hold_up_Radarr()
    {
        // Sonarr answers only once Radarr has been asked, so asking one after the other leaves Sonarr waiting.
        var radarrAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arr = Client(async request =>
        {
            if (request.RequestUri!.Host == "radarr")
            {
                radarrAsked.TrySetResult();
                return Answer(HttpStatusCode.OK, Queue("radarr-1", "Film.2024.1080p"));
            }

            bool asked = await Task.WhenAny(radarrAsked.Task, Task.Delay(TimeSpan.FromSeconds(5))) == radarrAsked.Task;
            return asked
                ? Answer(HttpStatusCode.OK, Queue("sonarr-1", "Show.S01E01.1080p"))
                : Answer(HttpStatusCode.ServiceUnavailable, "{\"message\":\"Radarr was never asked\"}");
        });
        var downloads = new DownloadsService(arr, _store);

        var (rows, errors, _) = await downloads.QueueAsync(CancellationToken.None);

        Assert.Empty(errors);
        Assert.Equal(new[] { "radarr", "sonarr" }, rows.Select(r => r.App).Order());
    }

    [Fact]
    public async Task A_timeout_says_the_app_didnt_answer_in_time()
    {
        // HttpClient reports its own time limit as a cancellation, which ServiceHttp turns into an error.
        var arr = Client(request => request.RequestUri!.Host == "radarr"
            ? Answer(HttpStatusCode.OK, Queue("radarr-1", "Film.2024.1080p"))
            : throw new TaskCanceledException());
        var downloads = new DownloadsService(arr, _store);

        var (rows, errors, _) = await downloads.QueueAsync(CancellationToken.None);

        Assert.Equal("Sonarr at http://sonarr:8989 didn't answer within 30 seconds.", errors["sonarr"]);
        Assert.Equal("radarr", Assert.Single(rows).App);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ArrClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer) => Client(request => Task.FromResult(answer(request)));

    private ArrClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) =>
        new(new Factory(new Answers(answer)), _store, NullLogger<ArrClient>.Instance);

    private static HttpResponseMessage Answer(HttpStatusCode status, string body, string type = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static HttpResponseMessage MovedTo(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    // As .NET's own redirect handler does: the request itself is moved on, and changed to a GET for a 301 or 302.
    private static HttpResponseMessage Redirected(HttpRequestMessage request, string to, HttpMethod method, string body, string type = "application/json")
    {
        request.RequestUri = new Uri(to);
        request.Method = method;
        var response = Answer(HttpStatusCode.OK, body, type);
        response.RequestMessage = request;
        return response;
    }

    // ArrClient as Jellyfin builds it: the host's factory, then the plugin's registrator, with the test's store.
    private ServiceProvider Registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        new PluginServiceRegistrator().RegisterServices(services, JellyfinHost.Create());
        services.AddSingleton(_store);
        return services.BuildServiceProvider();
    }

    private static string Queue(string downloadId, string title) =>
        $$"""{"totalRecords":1,"records":[{"id":1,"downloadId":"{{downloadId}}","title":"{{title}}","status":"downloading"}]}""";

    private sealed class Answers(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }

    // ArrClient disposes each client it gets, so each gets its own over the one handler.
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

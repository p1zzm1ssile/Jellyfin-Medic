using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicPicks;
using Jellyfin.Plugin.MedicPicks.Picks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

public sealed class SeerrClientTests : IDisposable
{
    private static readonly Guid Person = Guid.NewGuid();

    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly AccountPerHost _seerr = new(new Dictionary<string, int> { ["seerr-a"] = 42, ["seerr-b"] = 43 });
    private readonly HttpClient _http;
    private readonly SeerrClient _client;

    public SeerrClientTests()
    {
        var store = new PicksStore(new TestPaths(_root), NullLogger<PicksStore>.Instance);
        store.SetSeerrKey("seerr-key");
        _http = new HttpClient(_seerr);
        _client = new SeerrClient(new OneClient(_http), store, NullLogger<SeerrClient>.Instance);
    }

    [Fact]
    public async Task Another_Seerr_address_gets_its_own_account_lookup()
    {
        // An id remembered from the old server would make requests and approvals act as someone else on the new one.
        Assert.Equal(42, await _client.FindSeerrUserAsync("http://seerr-a:5055", Person, CancellationToken.None));

        Assert.Equal(43, await _client.FindSeerrUserAsync("http://seerr-b:5055", Person, CancellationToken.None));
        Assert.Equal(new[] { "seerr-a", "seerr-b" }, _seerr.Asked);
    }

    [Theory]
    [InlineData("http://seerr-a:5055/seerr/")]
    [InlineData("HTTP://SEERR-A:5055/seerr")]
    [InlineData(" http://seerr-a:5055/seerr ")]
    public async Task The_same_address_written_another_way_keeps_the_remembered_account(string sameAddress)
    {
        await _client.FindSeerrUserAsync("http://seerr-a:5055/seerr", Person, CancellationToken.None);

        Assert.Equal(42, await _client.FindSeerrUserAsync(sameAddress, Person, CancellationToken.None));
        Assert.Equal(new[] { "seerr-a" }, _seerr.Asked);
    }

    [Theory]
    [InlineData("seerr-a:5055")]
    [InlineData("seerr-a")]
    [InlineData("ftp://seerr-a:5055")]
    public async Task An_address_that_is_not_http_counts_as_Seerr_out_of_reach(string address)
    {
        // Saved before addresses got http:// added on save; My requests catches this one and says Seerr is out of reach.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.FindSeerrUserAsync(address, Person, CancellationToken.None));

        Assert.Contains("http:// or https://", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_seerr.Asked);
    }

    [Fact]
    public async Task Test_doesnt_follow_a_redirect_and_names_where_it_went()
    {
        var (client, sent) = Redirecting(HttpStatusCode.TemporaryRedirect, "https://login.example/?rd=seerr");

        var (ok, message) = await client.TestAsync("http://seerr-a:5055", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("https://login.example/?rd=seerr", message, StringComparison.Ordinal);
        Assert.Equal(new[] { "http://seerr-a:5055/api/v1/auth/me" }, sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task A_redirected_call_counts_as_Seerr_out_of_reach(HttpStatusCode status)
    {
        // Not followed, so the key and the request stay with the saved address; My requests says Seerr is out of reach.
        var (client, sent) = Redirecting(status, "http://elsewhere.example/api/v1/user/jellyfin/x");

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.FindSeerrUserAsync("http://seerr-a:5055", Person, CancellationToken.None));
        Assert.Null(await client.GetRequestsAsync("http://seerr-a:5055", null, "all", 10, CancellationToken.None));
        Assert.All(sent, url => Assert.StartsWith("http://seerr-a:5055/", url, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Test_through_the_registered_client_never_sends_the_key_on_to_where_a_redirect_points(int status)
    {
        // Real servers and the client the plugin registers: .NET drops Authorization on a redirect but sends X-Api-Key on.
        using var target = new LocalServer(_ => (200, null, "{}"));
        using var seerr = new LocalServer(request => (status, target.Url + "/seerr" + request.Url!.PathAndQuery, string.Empty));

        var (ok, message) = await RegisteredClient().TestAsync(seerr.Url, CancellationToken.None);

        Assert.Empty(target.Received);
        Assert.Equal("seerr-key", Assert.Single(seerr.Received).ApiKey);
        Assert.False(ok);
        Assert.Contains($"{seerr.Url} sent the request on to {target.Url}/seerr/api/v1/auth/me", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_request_through_the_registered_client_never_sends_the_key_on_to_where_a_redirect_points(int status)
    {
        // The account lookup answers; the request itself, a POST that a 307 or 308 would send again body and all, is sent on.
        using var target = new LocalServer(_ => (201, null, """{"id":7}"""));
        using var seerr = new LocalServer(request => request.HttpMethod == "GET"
            ? (200, null, """{"id":42}""")
            : (status, target.Url + request.Url!.PathAndQuery, string.Empty));

        var result = await RegisteredClient().RequestAsync(seerr.Url, Person, 603, "movie", CancellationToken.None);

        Assert.Empty(target.Received);
        Assert.Equal(new (string, string?)[] { ("GET", "seerr-key"), ("POST", "seerr-key") }, seerr.Received.Select(sent => (sent.Method, sent.ApiKey)));
        Assert.Equal("error", result.Status);
    }

    [Fact]
    public async Task The_registered_client_talks_to_Seerr_as_Jellyfin_does()
    {
        // Jellyfin's default client, which these calls went through before, names the server and asks for JSON first.
        using var seerr = new LocalServer(_ => (200, null, "{}"));

        var (ok, message) = await RegisteredClient().TestAsync(seerr.Url, CancellationToken.None);

        Assert.True(ok, message);
        var request = Assert.Single(seerr.Received);
        Assert.Equal("Jellyfin-Server/12.2.0", request.UserAgent);
        Assert.Equal("application/json; q=1.0, application/xml; q=0.9, */*; q=0.8", request.Accept);
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    // SeerrClient as Jellyfin builds it: the host's factory, then the plugin's registrator.
    private SeerrClient RegisteredClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        new PluginServiceRegistrator().RegisterServices(services, JellyfinHost.Create());
        var store = new PicksStore(new TestPaths(_root), NullLogger<PicksStore>.Instance);
        store.SetSeerrKey("seerr-key");
        return new SeerrClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), store, NullLogger<SeerrClient>.Instance);
    }

    private (SeerrClient Client, List<string> Sent) Redirecting(HttpStatusCode status, string location)
    {
        var sent = new List<string>();
        var http = new HttpClient(new Redirects(status, location, sent));
        var store = new PicksStore(new TestPaths(_root), NullLogger<PicksStore>.Instance);
        return (new SeerrClient(new OneClient(http), store, NullLogger<SeerrClient>.Instance), sent);
    }

    private sealed class Redirects(HttpStatusCode status, string location, List<string> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }

    /// <summary>A Seerr on each host, each with its own account id for the same Jellyfin user.</summary>
    private sealed class AccountPerHost(Dictionary<string, int> ids) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            Asked.Add(host);
            return Task.FromResult(ids.TryGetValue(host, out int id)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":" + id + "}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OneClient(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

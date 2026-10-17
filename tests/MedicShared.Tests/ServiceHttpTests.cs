using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace MedicShared.Tests;

public sealed class ServiceHttpTests
{
    [Fact]
    public async Task A_service_that_stops_answering_is_out_of_reach_not_a_cancelled_caller()
    {
        using var client = new HttpClient(new NeverAnswers());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://seerr:5055/api/v1/status");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ServiceHttp.SendAsync(client, request, TimeSpan.FromMilliseconds(200), "Seerr", CancellationToken.None));

        Assert.StartsWith("Seerr didn't answer within", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_caller_is_still_cancelled()
    {
        using var client = new HttpClient(new NeverAnswers());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://seerr:5055/api/v1/status");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServiceHttp.SendAsync(client, request, TimeSpan.FromSeconds(30), "Seerr", caller.Token));
    }

    [Fact]
    public async Task An_answer_in_time_comes_through()
    {
        using var client = new HttpClient(new Answers());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://sonarr:8989/api/v3/queue");

        using var response = await ServiceHttp.SendAsync(client, request, TimeSpan.FromSeconds(30), "Sonarr", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"message":"Not allowed"}""", "Not allowed")]
    [InlineData("""[{"propertyName":"Path","errorMessage":"Path is wrong"}]""", "Path is wrong")]
    [InlineData("""["Path is wrong"]""", null)]
    [InlineData("[]", null)]
    [InlineData("""{"message":1}""", null)]
    [InlineData("\"Not allowed\"", null)]
    [InlineData("<html>Sign in</html>", null)]
    [InlineData("", null)]
    public void The_service_s_own_words_are_read_from_an_error_answer(string body, string? expected) =>
        Assert.Equal(expected, ServiceHttp.ErrorMessage(body));

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task The_key_client_sends_the_key_and_body_only_to_the_address_asked(int status)
    {
        // A real server and the client Medic Picks and Medic Profiles register, as .NET follows a redirect to the
        // same host just as it does to another one, X-Api-Key and (for a 307 or 308) the body included.
        using var server = new Recorder(status);
        var services = new ServiceCollection();
        ServiceHttp.AddKeyClient(services, JellyfinHost.Create());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceHttp.KeyClient);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Address + "from") { Content = new StringContent("password=hunter2") };
        request.Headers.Add("X-Api-Key", "secret-key");

        var ex = await Assert.ThrowsAsync<RedirectedException>(() => ServiceHttp.SendAsync(client, request, TimeSpan.FromSeconds(10), "Sonarr", CancellationToken.None));

        Assert.Equal(new Uri(server.Address + "to"), ex.Target);
        Assert.Equal(new[] { "/from secret-key password=hunter2" }, server.Received);
    }

    [Fact]
    public async Task The_key_client_sends_the_headers_Jellyfin_s_default_client_sends()
    {
        // Calls with a key went through Jellyfin's default client before, which names the server and asks for JSON first.
        // Medic Picks and Medic Profiles each register the client, so the headers must not add up.
        using var server = new LocalServer(_ => (200, null, string.Empty));
        var services = new ServiceCollection();
        ServiceHttp.AddKeyClient(services, JellyfinHost.Create());
        ServiceHttp.AddKeyClient(services, JellyfinHost.Create());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceHttp.KeyClient);

        using var response = await client.GetAsync(new Uri(server.Url + "/"));

        var request = Assert.Single(server.Received);
        Assert.Equal("Jellyfin-Server/12.2.0", request.UserAgent);
        Assert.Equal("application/json; q=1.0, application/xml; q=0.9, */*; q=0.8", request.Accept);
    }

    [Theory]
    [InlineData("http://auth.example/login?rd=sonarr", "http://auth.example/login?rd=sonarr")]
    [InlineData("/sonarr/api/v3/queue", "http://sonarr:8989/sonarr/api/v3/queue")]
    public void A_redirect_names_where_it_points(string location, string expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);

        Assert.Equal(new Uri(expected), ServiceHttp.RedirectTarget(response, new Uri("http://sonarr:8989/api/v3/queue")));
    }

    [Fact]
    public void An_answer_that_isnt_a_redirect_names_nothing()
    {
        var asked = new Uri("http://sonarr:8989/api/v3/queue");
        using var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.Location = new Uri("http://elsewhere/");
        using var noLocation = new HttpResponseMessage(HttpStatusCode.Found);
        using var ok = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, asked) };

        Assert.Null(ServiceHttp.RedirectTarget(notModified, asked));
        Assert.Null(ServiceHttp.RedirectTarget(noLocation, asked));
        Assert.Null(ServiceHttp.RedirectTarget(ok, asked));
    }

    [Fact]
    public void An_answer_from_another_address_counts_as_a_redirect()
    {
        // From a client that followed the redirect itself: the key has gone, but the call still fails.
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://auth.example/login") };

        Assert.Equal(new Uri("http://auth.example/login"), ServiceHttp.RedirectTarget(response, new Uri("http://sonarr:8989/api/v3/queue")));
    }

    /// <summary>Answers /from with a redirect to /to on itself, and remembers each path, X-Api-Key and body it gets.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _status;

        public Recorder(int status)
        {
            _status = status;
            using (var free = new TcpListener(IPAddress.Loopback, 0))
            {
                free.Start();
                Address = $"http://127.0.0.1:{((IPEndPoint)free.LocalEndpoint).Port}/";
            }

            _listener.Prefixes.Add(Address);
            _listener.Start();
            _ = ServeAsync();
        }

        public string Address { get; }

        public List<string> Received { get; } = new();

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                lock (Received)
                {
                    Received.Add($"{context.Request.Url!.AbsolutePath} {context.Request.Headers["X-Api-Key"]} {body}");
                }

                context.Response.StatusCode = context.Request.Url.AbsolutePath == "/from" ? _status : 200;
                if (context.Request.Url.AbsolutePath == "/from")
                {
                    context.Response.RedirectLocation = Address + "to";
                }

                context.Response.Close();
            }
        }
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("never reached");
        }
    }

    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }
}

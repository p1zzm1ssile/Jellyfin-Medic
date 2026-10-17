using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

/// <summary>Searching Sonarr's or Radarr's library when a download's match is wrong.</summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class DownloadsSearchTests : IDisposable
{
    private const string Library = """
        [
          { "id": 1, "title": "The Long Film", "year": 2019 },
          { "id": 2, "title": "Long", "year": 2021 },
          { "id": 3, "title": "Something Else", "year": 2020 }
        ]
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;
    private readonly List<string> _asked = new();

    public DownloadsSearchTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Radarr, "radarr-key");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The whole library can be thousands of entries, so a search with nothing to match isn't worth fetching it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-.!")]
    public async Task A_search_with_nothing_to_match_doesnt_fetch_the_library(string query)
    {
        var (results, error) = await Service().SearchLibraryAsync(ArrApp.Radarr, query, CancellationToken.None);

        Assert.Null(error);
        Assert.Empty(results!);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task A_search_matches_titles_and_puts_the_closest_first()
    {
        var (results, error) = await Service().SearchLibraryAsync(ArrApp.Radarr, "long", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(new[] { 2, 1 }, results!.Select(r => r.Id));
        Assert.EndsWith("/api/v3/movie", Assert.Single(_asked), StringComparison.Ordinal);
    }

    private DownloadsService Service()
    {
        var arr = new ArrClient(new Factory(new Answers(request =>
        {
            _asked.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Library, Encoding.UTF8, "application/json"), RequestMessage = request });
        })), _store, NullLogger<ArrClient>.Instance);
        return new DownloadsService(arr, _store);
    }

    private sealed class Answers(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

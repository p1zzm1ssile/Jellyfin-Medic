using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

// Plugin.Instance is static and ArrClientTests sets up its own, so the tests in this collection run
// apart from the other test classes instead of alongside them.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceCollection
{
    public const string Name = "Plugin.Instance";
}

// Sonarr and Radarr have no indexer status route (only Prowlarr does), so the failing indexers come
// from their health checks. Those messages are translated, and the indexer names always come last.
[Collection(PluginInstanceCollection.Name)]
public sealed class IndexersServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;

    public IndexersServiceTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.SonarrUrl = "http://sonarr:8989";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
    }

    [Fact]
    public async Task A_failing_indexer_is_found_in_a_translated_health_warning()
    {
        var sonarr = new FakeSonarr(
            [Indexer(1, "Nyaa.si"), Indexer(2, "Nyaa"), Unused(3, "Unused")],
            [Health("IndexerStatusCheck", "warning", "Indexadores indisponíveis devido a falhas: Nyaa.si")]);

        var rows = await RowsAsync(sonarr);

        Assert.Multiple(
            () => Assert.Equal(new[] { ("Nyaa", "ok"), ("Nyaa.si", "failing"), ("Unused", "off") }, States(rows)),
            () => Assert.DoesNotContain("/api/v3/indexerstatus", sonarr.Requested));
    }

    [Fact]
    public async Task An_error_marks_every_used_indexer_failing()
    {
        // Sonarr sends an error, naming no indexer, once every indexer it uses is failing. NZB Geek
        // does manual searches only, which still counts as used.
        var sonarr = new FakeSonarr(
            [Indexer(1, "Nyaa.si"), Indexer(2, "NZB Geek", rss: false, automatic: false), Unused(3, "Unused")],
            [Health("IndexerStatusCheck", "error", "All indexers are unavailable due to failures")]);

        var rows = await RowsAsync(sonarr);

        Assert.Equal(new[] { ("NZB Geek", "failing"), ("Nyaa.si", "failing"), ("Unused", "off") }, States(rows));
    }

    [Fact]
    public async Task A_long_term_failure_says_so_in_the_detail()
    {
        // Sonarr lists indexers failing for less than 6 hours and for longer in two separate checks.
        // The detail is in the page's own words, whatever language Sonarr's message is in.
        var sonarr = new FakeSonarr(
            [Indexer(1, "Nyaa.si"), Indexer(2, "NZB Geek"), Indexer(3, "Nyaa")],
            [
                Health("IndexerStatusCheck", "warning", "Indexadores indisponíveis devido a falhas: NZB Geek"),
                Health("IndexerLongTermStatusCheck", "warning", "Indexadores indisponíveis devido a falhas por mais de 6 horas: Nyaa.si")
            ]);

        var rows = await RowsAsync(sonarr);

        Assert.Equal(new[] { ("NZB Geek", "failing"), ("Nyaa", "ok"), ("Nyaa.si", "failing") }, States(rows));
        Assert.Contains("6 hours", Detail(rows, "Nyaa.si"));
        Assert.DoesNotContain("6 hours", Detail(rows, "NZB Geek"));
    }

    [Fact]
    public async Task A_name_at_the_end_of_a_sentence_still_matches()
    {
        // Sonarr's Finnish message ends with a full stop after the names.
        var sonarr = new FakeSonarr(
            [Indexer(1, "Nyaa.si"), Indexer(2, "Nyaa")],
            [Health("IndexerStatusCheck", "warning", "Hakupalvelut eivät ole virheiden vuoksi käytettävissä: Nyaa.si.")]);

        var rows = await RowsAsync(sonarr);

        Assert.Equal(new[] { ("Nyaa", "ok"), ("Nyaa.si", "failing") }, States(rows));
    }

    [Fact]
    public async Task Other_indexer_checks_do_not_mark_an_indexer_failing()
    {
        // Nyaa does searches only, so Sonarr also sends an error saying no indexer does RSS.
        var sonarr = new FakeSonarr(
            [Indexer(1, "Nyaa", rss: false)],
            [
                Health("IndexerJackettAllCheck", "warning", "Indexers using the unsupported Jackett 'all' endpoint: Nyaa"),
                Health("IndexerRssCheck", "error", "No indexers available with RSS sync enabled, Sonarr will not grab new releases automatically")
            ]);

        var rows = await RowsAsync(sonarr);

        Assert.Equal(new[] { ("Nyaa", "ok") }, States(rows));
    }

    [Fact]
    public async Task A_shorter_name_inside_a_longer_one_is_not_failing()
    {
        var sonarr = new FakeSonarr(
            [Indexer(1, "Geek"), Indexer(2, "NZB Geek")],
            [Health("IndexerStatusCheck", "warning", "Indexers unavailable due to failures: NZB Geek")]);

        var rows = await RowsAsync(sonarr);

        Assert.Equal(new[] { ("Geek", "ok"), ("NZB Geek", "failing") }, States(rows));
    }

    [Fact]
    public async Task Every_indexer_the_warning_lists_is_failing()
    {
        // Sonarr puts ", " between the names.
        var sonarr = new FakeSonarr(
            [Indexer(1, "NZB Geek"), Indexer(2, "Nyaa.si"), Indexer(3, "TorrentLeech")],
            [Health("IndexerStatusCheck", "warning", "Indexers unavailable due to failures: NZB Geek, Nyaa.si")]);

        var rows = await RowsAsync(sonarr);

        Assert.Equal(new[] { ("NZB Geek", "failing"), ("Nyaa.si", "failing"), ("TorrentLeech", "ok") }, States(rows));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static object Indexer(int id, string name, bool rss = true, bool automatic = true, bool interactive = true) =>
        new { id, name, enableRss = rss, enableAutomaticSearch = automatic, enableInteractiveSearch = interactive };

    private static object Unused(int id, string name) => Indexer(id, name, rss: false, automatic: false, interactive: false);

    // As /api/v3/health lists them: the source is the check's class name, and the type is in lower case.
    private static object Health(string source, string type, string message) =>
        new { source, type, message, wikiUrl = "https://wiki.servarr.com/sonarr/system" };

    private async Task<List<IndexerRow>> RowsAsync(FakeSonarr sonarr)
    {
        var arr = new ArrClient(new Factory(sonarr), _store, NullLogger<ArrClient>.Instance);
        var (rows, _, _) = await new IndexersService(arr).StatusAsync(CancellationToken.None);
        return rows;
    }

    // Each indexer's name and state, by name, so the checks don't depend on the order the tab shows them in.
    private static IEnumerable<(string Name, string State)> States(IEnumerable<IndexerRow> rows) =>
        rows.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => (r.Name, r.State));

    private static string Detail(IEnumerable<IndexerRow> rows, string name) => rows.Single(r => r.Name == name).Detail;

    // A pretend Sonarr: its indexers and its health checks, and 404 for any other route, as Sonarr answers.
    private sealed class FakeSonarr(object[] indexers, object[] health) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            object[]? answer = path switch
            {
                "/api/v3/indexer" => indexers,
                "/api/v3/health" => health,
                _ => null
            };
            return Task.FromResult(answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") });
        }
    }

    // ArrClient disposes each client it gets, so each gets its own over the one handler.
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

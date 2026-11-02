using Jellyfin.Plugin.MedicPicks.Picks;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

public sealed class TmdbClientTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_TMDb_that_stops_answering_is_unavailable_not_a_cancelled_run()
    {
        // The HTTP client's own time limit runs out here; the run itself is never cancelled.
        using var client = new HttpClient(new NeverAnswers()) { Timeout = TimeSpan.FromSeconds(1) };
        var tmdb = new TmdbClient(new OneClient(client), NullLogger<TmdbClient>.Instance);

        await Assert.ThrowsAsync<TmdbUnavailableException>(() => tmdb.GetRecommendationsAsync("movie", "603", Key, "en-GB", CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_the_run_still_cancels()
    {
        using var client = new HttpClient(new NeverAnswers());
        var tmdb = new TmdbClient(new OneClient(client), NullLogger<TmdbClient>.Instance);
        using var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tmdb.GetRecommendationsAsync("movie", "603", Key, "en-GB", run.Token));
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("never reached");
        }
    }

    private sealed class OneClient(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

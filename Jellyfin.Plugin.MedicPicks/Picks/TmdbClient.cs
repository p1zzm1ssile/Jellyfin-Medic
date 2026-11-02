using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;
using static Medic.Shared.Json;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>TMDb couldn't answer (refused key, rate limit, server error, no connection): not the same as "no recommendations".</summary>
public class TmdbUnavailableException : Exception
{
    public TmdbUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Calls TMDb's /recommendations endpoint. Accepts either a v3 API key or a v4 read access token.
/// </summary>
public class TmdbClient
{
    // TMDb answers within a second or two; one that has stopped answering counts as unavailable after this.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbClient> _logger;

    public TmdbClient(IHttpClientFactory httpClientFactory, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <param name="mediaType">"movie" or "tv".</param>
    public async Task<IReadOnlyList<TmdbTitle>> GetRecommendationsAsync(
        string mediaType,
        string tmdbId,
        string apiKey,
        string language,
        CancellationToken cancellationToken)
    {
        var results = new List<TmdbTitle>();
        var isBearer = apiKey.StartsWith("eyJ", StringComparison.Ordinal); // v4 tokens are JWTs

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "https://api.themoviedb.org/3/{0}/{1}/recommendations?language={2}&page=1",
            mediaType,
            Uri.EscapeDataString(tmdbId),
            Uri.EscapeDataString(string.IsNullOrWhiteSpace(language) ? "en-GB" : language));

        if (!isBearer)
        {
            url += "&api_key=" + Uri.EscapeDataString(apiKey);
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(CallTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (isBearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return results; // TMDb doesn't know this title: it simply has no recommendations
            }

            if (!response.IsSuccessStatusCode)
            {
                // Never log or show the URL: it may contain the key.
                throw new TmdbUnavailableException($"TMDb answered {(int)response.StatusCode} for {mediaType} {tmdbId}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                {
                    continue;
                }

                var title = Text(item, mediaType == "tv" ? "name" : "title") ?? Text(item, "title") ?? Text(item, "name");
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var date = Text(item, mediaType == "tv" ? "first_air_date" : "release_date");
                int? year = null;
                if (!string.IsNullOrEmpty(date) && date.Length >= 4
                    && int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                {
                    year = y;
                }

                double vote = 0;
                if (item.TryGetProperty("vote_average", out var voteEl) && voteEl.ValueKind == JsonValueKind.Number)
                {
                    vote = voteEl.GetDouble();
                }

                results.Add(new TmdbTitle
                {
                    Id = id,
                    MediaType = mediaType,
                    Title = title!,
                    Year = year,
                    Overview = Text(item, "overview"),
                    PosterPath = Text(item, "poster_path"),
                    OriginalLanguage = Text(item, "original_language"),
                    GenreIds = item.TryGetProperty("genre_ids", out var genres) && genres.ValueKind == JsonValueKind.Array
                        ? genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Number).Select(g => g.GetInt32()).ToList()
                        : new List<int>(),
                    VoteAverage = vote
                });
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the run being cancelled: TMDb didn't answer in time (this call's limit or the HTTP client's own).
            // A cancelled run isn't caught here, so it still ends the run.
            throw new TmdbUnavailableException($"TMDb didn't answer in time for {mediaType} {tmdbId}", ex);
        }
        catch (TmdbUnavailableException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new TmdbUnavailableException($"TMDb couldn't be reached for {mediaType} {tmdbId}", ex);
        }
        catch (JsonException ex)
        {
            // An answer that isn't JSON: treat this title as having no recommendations.
            _logger.LogWarning(ex, "Medic Picks: TMDb sent something unexpected for {Type} {Id}", mediaType, tmdbId);
        }

        return results;
    }
}

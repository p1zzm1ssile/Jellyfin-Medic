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

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Calls TMDb's /recommendations endpoint. Accepts either a v3 API key or a v4 read access token.
/// </summary>
public class TmdbClient
{
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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (isBearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Never log the URL: it may contain the key.
                _logger.LogWarning(
                    "Medic Picks: TMDb returned {Status} for {Type} {Id}",
                    (int)response.StatusCode,
                    mediaType,
                    tmdbId);
                return results;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

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

                var title = GetString(item, mediaType == "tv" ? "name" : "title") ?? GetString(item, "title") ?? GetString(item, "name");
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var date = GetString(item, mediaType == "tv" ? "first_air_date" : "release_date");
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
                    Overview = GetString(item, "overview"),
                    PosterPath = GetString(item, "poster_path"),
                    OriginalLanguage = GetString(item, "original_language"),
                    GenreIds = item.TryGetProperty("genre_ids", out var genres) && genres.ValueKind == JsonValueKind.Array
                        ? genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Number).Select(g => g.GetInt32()).ToList()
                        : new List<int>(),
                    VoteAverage = vote
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: TMDb request failed for {Type} {Id}", mediaType, tmdbId);
        }

        return results;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

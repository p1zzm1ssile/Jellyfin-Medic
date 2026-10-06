using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>One download, as the Downloads tab shows it. A season pack is one row, not one per episode.</summary>
public class DownloadRow
{
    public string App { get; set; } = "radarr";

    /// <summary>Every Sonarr/Radarr queue id in this download (several for a season pack).</summary>
    public List<int> QueueIds { get; set; } = new();

    public string DownloadId { get; set; } = string.Empty;

    /// <summary>What it's meant to be: "Dune: Part Two (2024)" or "Frieren – S01E03".</summary>
    public string MediaTitle { get; set; } = string.Empty;

    /// <summary>The release name as the indexer had it.</summary>
    public string ReleaseTitle { get; set; } = string.Empty;

    public string? Quality { get; set; }

    public long Size { get; set; }

    public long SizeLeft { get; set; }

    public int? Progress { get; set; }

    public DateTime? EtaUtc { get; set; }

    public DateTime? AddedUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? TrackedState { get; set; }

    /// <summary>ok, problem or stuck.</summary>
    public string Health { get; set; } = "ok";

    /// <summary>Plain-English summary of what Sonarr or Radarr says is wrong.</summary>
    public string? Problem { get; set; }

    public List<string> Messages { get; set; } = new();

    public string? DownloadClient { get; set; }

    public string? Indexer { get; set; }

    public string? Protocol { get; set; }

    /// <summary>The files are there to import by hand.</summary>
    public bool CanImport { get; set; }

    /// <summary>Not matched to anything in Sonarr or Radarr.</summary>
    public bool Unknown { get; set; }
}

public class BlockedRow
{
    public string App { get; set; } = "radarr";

    public int Id { get; set; }

    public string ReleaseTitle { get; set; } = string.Empty;

    public string? MediaTitle { get; set; }

    public DateTime? DateUtc { get; set; }

    public string? Quality { get; set; }

    public string? Indexer { get; set; }

    public string? Message { get; set; }
}

/// <summary>A file Sonarr or Radarr found in a download, for importing by hand.</summary>
public class ImportFile
{
    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? Quality { get; set; }

    public string? Languages { get; set; }

    /// <summary>What Sonarr/Radarr thinks it is. Movie or series id; 0 when it couldn't tell.</summary>
    public int MediaId { get; set; }

    public string? MediaTitle { get; set; }

    public int? SeasonNumber { get; set; }

    public List<EpisodeChoice> Episodes { get; set; } = new();

    public List<string> Rejections { get; set; } = new();

    /// <summary>Sent back unchanged when importing, so the quality and languages Sonarr/Radarr detected are kept.</summary>
    public JsonElement? QualityRaw { get; set; }

    public JsonElement? LanguagesRaw { get; set; }

    public string? ReleaseGroup { get; set; }

    public int IndexerFlags { get; set; }

    public string? ReleaseType { get; set; }
}

public class EpisodeChoice
{
    public int Id { get; set; }

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public string? Title { get; set; }

    public bool HasFile { get; set; }
}

public class MediaChoice
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }
}

/// <summary>What the page sends to import one file.</summary>
public class ImportFileRequest
{
    public string Path { get; set; } = string.Empty;

    public int MediaId { get; set; }

    public List<int> EpisodeIds { get; set; } = new();

    public JsonElement? QualityRaw { get; set; }

    public JsonElement? LanguagesRaw { get; set; }

    public string? ReleaseGroup { get; set; }

    public int IndexerFlags { get; set; }

    public string? ReleaseType { get; set; }
}

/// <summary>
/// The Downloads tab: Sonarr and Radarr's download queues in one list, with Remove, Remove and
/// block, importing by hand, and the blocked list. Nothing happens until an admin presses a button.
/// </summary>
public class DownloadsService
{
    private const int PageSize = 200;
    private const int MaxPages = 5;

    // Sonarr and Radarr's own words for these, and ours.
    private static readonly Dictionary<string, string> StateWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["importBlocked"] = "Finished, but couldn't be imported",
        ["importPending"] = "Finished, waiting to be imported",
        ["failedPending"] = "Failed, waiting to be removed",
        ["failed"] = "Failed",
    };

    private static readonly Dictionary<string, string> StatusWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warning"] = "The download client reports a problem",
        ["failed"] = "Failed in the download client",
        ["downloadClientUnavailable"] = "The download client can't be reached",
        ["paused"] = "Paused in the download client",
        ["queued"] = "Queued in the download client",
        ["delay"] = "Waiting (delay profile)",
        ["fallback"] = "Waiting for a fallback",
    };

    private readonly ArrClient _arr;
    private readonly ArrStore _store;

    // Movie and series lists change slowly and can be large, so they're kept briefly.
    private readonly Dictionary<ArrApp, (List<MediaChoice> List, DateTime At)> _library = new();

    public DownloadsService(ArrClient arr, ArrStore store)
    {
        _arr = arr;
        _store = store;
    }

    // ---------- Queue ----------

    /// <summary>Both queues, problems first. Errors are per app, so one being down doesn't hide the other.</summary>
    public async Task<(List<DownloadRow> Rows, Dictionary<string, string> Errors)> QueueAsync(CancellationToken ct)
    {
        var rows = new List<DownloadRow>();
        var errors = new Dictionary<string, string>();
        foreach (var app in new[] { ArrApp.Sonarr, ArrApp.Radarr })
        {
            if (!_arr.IsConfigured(app))
            {
                continue;
            }

            var (records, error) = await QueueRecordsAsync(app, ct).ConfigureAwait(false);
            if (error is not null)
            {
                errors[Key(app)] = error;
                continue;
            }

            rows.AddRange(Group(app, records));
        }

        int stuckHours = Math.Max(1, Plugin.Instance?.Configuration.StuckAfterHours ?? 6);
        foreach (var row in rows)
        {
            Judge(row, stuckHours);
        }

        return (rows
            .OrderBy(r => r.Health == "problem" ? 0 : r.Health == "stuck" ? 1 : 2)
            .ThenBy(r => r.AddedUtc ?? DateTime.MaxValue)
            .ToList(), errors);
    }

    private async Task<(List<JsonElement> Records, string? Error)> QueueRecordsAsync(ArrApp app, CancellationToken ct)
    {
        var records = new List<JsonElement>();
        string include = app == ArrApp.Sonarr
            ? "includeSeries=true&includeEpisode=true&includeUnknownSeriesItems=true"
            : "includeMovie=true&includeUnknownMovieItems=true";
        for (int page = 1; page <= MaxPages; page++)
        {
            using var result = await _arr.GetAsync(app, $"queue?page={page}&pageSize={PageSize}&{include}", ct).ConfigureAwait(false);
            if (!result.Ok || result.Json is null)
            {
                return (records, result.Ok ? $"{ArrClient.Name(app)} sent something unexpected." : result.Message);
            }

            var root = result.Json.RootElement;
            if (root.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                // Clone so the rows outlive this page's document.
                records.AddRange(list.EnumerateArray().Select(e => e.Clone()));
            }

            int total = Json.Int(root, "totalRecords") ?? 0;
            if (page * PageSize >= total)
            {
                break;
            }
        }

        return (records, null);
    }

    private static IEnumerable<DownloadRow> Group(ArrApp app, List<JsonElement> records)
    {
        // A download with no id (rare) stays on its own.
        foreach (var group in records.GroupBy(r => Json.Text(r, "downloadId") ?? "#" + Json.Int(r, "id")))
        {
            var first = group.First();
            var row = new DownloadRow
            {
                App = Key(app),
                DownloadId = Json.Text(first, "downloadId") ?? string.Empty,
                ReleaseTitle = Json.Text(first, "title") ?? string.Empty,
                Quality = QualityName(first),
                Size = Json.Long(first, "size"),
                SizeLeft = Json.Long(first, "sizeleft"),
                EtaUtc = Json.Date(first, "estimatedCompletionTime"),
                AddedUtc = Json.Date(first, "added"),
                Status = Json.Text(first, "status") ?? string.Empty,
                TrackedState = Json.Text(first, "trackedDownloadState"),
                DownloadClient = Json.Text(first, "downloadClient"),
                Indexer = Json.Text(first, "indexer"),
                Protocol = Json.Text(first, "protocol"),
            };
            row.QueueIds = group.Select(r => Json.Int(r, "id") ?? 0).Where(i => i > 0).Distinct().ToList();
            row.MediaTitle = MediaTitle(app, group.ToList(), out bool unknown);
            row.Unknown = unknown;
            row.Progress = row.Size > 0 ? (int)Math.Clamp(Math.Round(100.0 * (row.Size - row.SizeLeft) / row.Size), 0, 100) : null;

            // Sonarr and Radarr's own explanations, without repeats.
            var messages = new List<string>();
            if (Json.Text(first, "errorMessage") is { Length: > 0 } error)
            {
                messages.Add(error);
            }

            foreach (var r in group)
            {
                if (r.TryGetProperty("statusMessages", out var sm) && sm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in sm.EnumerateArray())
                    {
                        if (m.TryGetProperty("messages", out var lines) && lines.ValueKind == JsonValueKind.Array)
                        {
                            messages.AddRange(lines.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!));
                        }
                    }
                }
            }

            row.Messages = messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Take(8).ToList();
            yield return row;
        }
    }

    private static void Judge(DownloadRow row, int stuckHours)
    {
        bool trackedProblem = row.TrackedState is "importBlocked" or "failedPending" or "failed";
        bool statusProblem = row.Status is "warning" or "failed" or "downloadClientUnavailable";
        row.CanImport = row.TrackedState is "importBlocked" or "importPending" || row.Status == "completed";

        if (trackedProblem || statusProblem || row.Unknown)
        {
            row.Health = "problem";
            row.Problem = row.Unknown
                ? "Not matched to anything in " + (row.App == "sonarr" ? "Sonarr" : "Radarr") + ". It may be the wrong download, or need importing by hand."
                : StateWords.GetValueOrDefault(row.TrackedState ?? string.Empty) ?? StatusWords.GetValueOrDefault(row.Status) ?? "Something's wrong with this download";
            return;
        }

        // Still "downloading" long after it was added, with no finish time: probably stalled (no seeds, dead link).
        bool old = row.AddedUtc is { } added && DateTime.UtcNow - added > TimeSpan.FromHours(stuckHours);
        bool noEta = row.EtaUtc is null || row.EtaUtc < DateTime.UtcNow;
        if (old && noEta && row.Status is "downloading" or "queued" or "paused" && row.SizeLeft > 0)
        {
            row.Health = "stuck";
            row.Problem = string.Format(CultureInfo.InvariantCulture, "No progress estimate after {0} hours. It may have stalled.", stuckHours);
            return;
        }

        row.Health = "ok";
        row.Problem = StatusWords.GetValueOrDefault(row.Status) ?? StateWords.GetValueOrDefault(row.TrackedState ?? string.Empty);
    }

    private static string MediaTitle(ArrApp app, List<JsonElement> records, out bool unknown)
    {
        var first = records[0];
        unknown = false;
        if (app == ArrApp.Radarr)
        {
            if (first.TryGetProperty("movie", out var movie) && movie.ValueKind == JsonValueKind.Object)
            {
                return WithYear(Json.Text(movie, "title"), Json.Int(movie, "year"));
            }

            unknown = (Json.Int(first, "movieId") ?? 0) == 0;
            return unknown ? "Unknown film" : "A film";
        }

        string series = first.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.Object
            ? Json.Text(s, "title") ?? "A series"
            : string.Empty;
        if (series.Length == 0)
        {
            unknown = (Json.Int(first, "seriesId") ?? 0) == 0;
            return unknown ? "Unknown series" : "A series";
        }

        var episodes = records
            .Select(r => r.TryGetProperty("episode", out var e) && e.ValueKind == JsonValueKind.Object ? e : (JsonElement?)null)
            .Where(e => e is not null)
            .Select(e => (Season: Json.Int(e!.Value, "seasonNumber") ?? 0, Episode: Json.Int(e.Value, "episodeNumber") ?? 0, Title: Json.Text(e.Value, "title")))
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .ToList();
        if (episodes.Count == 1)
        {
            var e = episodes[0];
            return string.Format(CultureInfo.InvariantCulture, "{0} – S{1:00}E{2:00}{3}", series, e.Season, e.Episode, string.IsNullOrWhiteSpace(e.Title) ? string.Empty : " " + e.Title);
        }

        if (episodes.Count > 1)
        {
            var seasons = episodes.Select(e => e.Season).Distinct().ToList();
            return seasons.Count == 1
                ? string.Format(CultureInfo.InvariantCulture, "{0} – season {1}, {2} episodes", series, seasons[0], episodes.Count)
                : string.Format(CultureInfo.InvariantCulture, "{0} – {1} episodes", series, episodes.Count);
        }

        return series;
    }

    // ---------- Remove and block ----------

    /// <summary>
    /// Removes a download from Sonarr or Radarr and the download client. With block, the release is
    /// blocklisted so it's never grabbed again, and (unless told not to) a search starts for another.
    /// </summary>
    public async Task<(bool Ok, string Message)> RemoveAsync(ArrApp app, List<int> queueIds, bool block, bool searchAgain, string title, string? by, CancellationToken ct)
    {
        if (queueIds.Count == 0)
        {
            return (false, "Nothing to remove.");
        }

        string query = "queue/bulk?removeFromClient=true&blocklist=" + (block ? "true" : "false")
            + "&skipRedownload=" + (block && searchAgain ? "false" : "true");
        using var result = await _arr.DeleteAsync(app, query, new { ids = queueIds }, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, result.Message);
        }

        _store.Record(new ActionRecord
        {
            Utc = DateTime.UtcNow,
            App = Key(app),
            Action = block ? "block" : "remove",
            Title = title,
            Detail = block ? (searchAgain ? "Blocked, searching for another release" : "Blocked, no new search") : "Removed",
            By = by
        });
        return (true, block
            ? (searchAgain ? "Removed and blocked. " + ArrClient.Name(app) + " is looking for a different release." : "Removed and blocked.")
            : "Removed.");
    }

    // ---------- Blocked list ----------

    public async Task<(List<BlockedRow> Rows, Dictionary<string, string> Errors)> BlockedAsync(CancellationToken ct)
    {
        var rows = new List<BlockedRow>();
        var errors = new Dictionary<string, string>();
        foreach (var app in new[] { ArrApp.Sonarr, ArrApp.Radarr })
        {
            if (!_arr.IsConfigured(app))
            {
                continue;
            }

            using var result = await _arr.GetAsync(app, "blocklist?page=1&pageSize=100&sortKey=date&sortDirection=descending", ct).ConfigureAwait(false);
            if (!result.Ok || result.Json is null)
            {
                errors[Key(app)] = result.Ok ? ArrClient.Name(app) + " sent something unexpected." : result.Message;
                continue;
            }

            if (!result.Json.RootElement.TryGetProperty("records", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var r in list.EnumerateArray())
            {
                string? media = null;
                if (r.TryGetProperty("movie", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    media = WithYear(Json.Text(m, "title"), Json.Int(m, "year"));
                }
                else if (r.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    media = Json.Text(s, "title");
                }

                rows.Add(new BlockedRow
                {
                    App = Key(app),
                    Id = Json.Int(r, "id") ?? 0,
                    ReleaseTitle = Json.Text(r, "sourceTitle") ?? string.Empty,
                    MediaTitle = media,
                    DateUtc = Json.Date(r, "date"),
                    Quality = QualityName(r),
                    Indexer = Json.Text(r, "indexer"),
                    Message = Json.Text(r, "message")
                });
            }
        }

        return (rows.OrderByDescending(r => r.DateUtc).ToList(), errors);
    }

    public async Task<(bool Ok, string Message)> UnblockAsync(ArrApp app, int id, string title, string? by, CancellationToken ct)
    {
        using var result = await _arr.DeleteAsync(app, "blocklist/" + id.ToString(CultureInfo.InvariantCulture), null, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, result.Message);
        }

        _store.Record(new ActionRecord { Utc = DateTime.UtcNow, App = Key(app), Action = "unblock", Title = title, By = by });
        return (true, "Unblocked. It can be grabbed again.");
    }

    // ---------- Import by hand ----------

    /// <summary>The files in a download, with what Sonarr or Radarr thinks each one is and why it wasn't imported.</summary>
    public async Task<(List<ImportFile>? Files, string? Error)> ImportFilesAsync(ArrApp app, string downloadId, CancellationToken ct)
    {
        using var result = await _arr.GetAsync(app, "manualimport?downloadId=" + Uri.EscapeDataString(downloadId) + "&filterExistingFiles=false", ct).ConfigureAwait(false);
        if (!result.Ok || result.Json is null || result.Json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (null, result.Ok ? "No files were found for this download. It may still be downloading or unpacking." : result.Message);
        }

        var files = new List<ImportFile>();
        foreach (var f in result.Json.RootElement.EnumerateArray())
        {
            var file = new ImportFile
            {
                Path = Json.Text(f, "path") ?? string.Empty,
                Name = Json.Text(f, "relativePath") ?? Json.Text(f, "name") ?? string.Empty,
                Size = Json.Long(f, "size"),
                Quality = QualityName(f),
                QualityRaw = f.TryGetProperty("quality", out var q) ? q.Clone() : null,
                LanguagesRaw = f.TryGetProperty("languages", out var l) ? l.Clone() : null,
                Languages = f.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", langs.EnumerateArray().Select(x => Json.Text(x, "name")).Where(x => !string.IsNullOrEmpty(x)))
                    : null,
                ReleaseGroup = Json.Text(f, "releaseGroup"),
                IndexerFlags = Json.Int(f, "indexerFlags") ?? 0,
                ReleaseType = Json.Text(f, "releaseType"),
                SeasonNumber = Json.Int(f, "seasonNumber")
            };

            if (app == ArrApp.Radarr && f.TryGetProperty("movie", out var movie) && movie.ValueKind == JsonValueKind.Object)
            {
                file.MediaId = Json.Int(movie, "id") ?? 0;
                file.MediaTitle = WithYear(Json.Text(movie, "title"), Json.Int(movie, "year"));
            }
            else if (app == ArrApp.Sonarr && f.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Object)
            {
                file.MediaId = Json.Int(series, "id") ?? 0;
                file.MediaTitle = Json.Text(series, "title");
            }

            if (f.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                file.Episodes = eps.EnumerateArray().Select(EpisodeFrom).ToList();
            }

            if (f.TryGetProperty("rejections", out var rej) && rej.ValueKind == JsonValueKind.Array)
            {
                file.Rejections = rej.EnumerateArray()
                    .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : Json.Text(r, "reason"))
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r!)
                    .Distinct()
                    .ToList();
            }

            if (file.Path.Length > 0)
            {
                files.Add(file);
            }
        }

        return files.Count == 0
            ? (null, "No video files were found in this download. It may be a password-protected archive, still unpacking, or not a video at all.")
            : (files, null);
    }

    /// <summary>Films or series in Sonarr or Radarr whose title matches, for when the match is wrong.</summary>
    public async Task<(List<MediaChoice>? Results, string? Error)> SearchLibraryAsync(ArrApp app, string query, CancellationToken ct)
    {
        List<MediaChoice>? all = null;
        lock (_library)
        {
            if (_library.TryGetValue(app, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            {
                all = cached.List;
            }
        }

        if (all is null)
        {
            using var result = await _arr.GetAsync(app, app == ArrApp.Radarr ? "movie" : "series", ct).ConfigureAwait(false);
            if (!result.Ok || result.Json is null || result.Json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (null, result.Ok ? ArrClient.Name(app) + " sent something unexpected." : result.Message);
            }

            all = result.Json.RootElement.EnumerateArray()
                .Select(m => new MediaChoice { Id = Json.Int(m, "id") ?? 0, Title = Json.Text(m, "title") ?? string.Empty, Year = Json.Int(m, "year") })
                .Where(m => m.Id > 0 && m.Title.Length > 0)
                .ToList();
            lock (_library)
            {
                _library[app] = (all, DateTime.UtcNow);
            }
        }

        string q = Simplify(query);
        if (q.Length == 0)
        {
            return (new List<MediaChoice>(), null);
        }

        return (all
            .Select(m => (m, s: Simplify(m.Title)))
            .Where(x => x.s.Contains(q, StringComparison.Ordinal))
            .OrderBy(x => x.s.StartsWith(q, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.s.Length)
            .Take(20)
            .Select(x => x.m)
            .ToList(), null);
    }

    /// <summary>A series' episodes, for choosing which ones a file is.</summary>
    public async Task<(List<EpisodeChoice>? Episodes, string? Error)> EpisodesAsync(int seriesId, CancellationToken ct)
    {
        using var result = await _arr.GetAsync(ArrApp.Sonarr, "episode?seriesId=" + seriesId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (!result.Ok || result.Json is null || result.Json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (null, result.Ok ? "Sonarr sent something unexpected." : result.Message);
        }

        return (result.Json.RootElement.EnumerateArray().Select(EpisodeFrom)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList(), null);
    }

    /// <summary>
    /// Imports the chosen files, as Sonarr or Radarr's own Manual Import does. Only files that are
    /// actually in this download are accepted.
    /// </summary>
    public async Task<(bool Ok, string Message)> ImportAsync(ArrApp app, string downloadId, string importMode, List<ImportFileRequest> files, string title, string? by, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return (false, "Choose at least one file to import.");
        }

        var (available, error) = await ImportFilesAsync(app, downloadId, ct).ConfigureAwait(false);
        if (available is null)
        {
            return (false, error ?? "The download's files couldn't be read.");
        }

        var known = new HashSet<string>(available.Select(f => f.Path), StringComparer.Ordinal);
        var list = new JsonArray();
        foreach (var f in files)
        {
            if (!known.Contains(f.Path))
            {
                return (false, "That file isn't part of this download any more. Open Import again to refresh.");
            }

            if (f.MediaId <= 0 || (app == ArrApp.Sonarr && f.EpisodeIds.Count == 0))
            {
                return (false, app == ArrApp.Sonarr ? "Choose the series and episodes for every file you're importing." : "Choose the film for every file you're importing.");
            }

            var item = new JsonObject
            {
                ["path"] = f.Path,
                ["downloadId"] = downloadId,
                ["releaseGroup"] = f.ReleaseGroup,
                ["indexerFlags"] = f.IndexerFlags,
                ["quality"] = f.QualityRaw is { } q ? JsonNode.Parse(q.GetRawText()) : null,
                ["languages"] = f.LanguagesRaw is { } l ? JsonNode.Parse(l.GetRawText()) : new JsonArray()
            };
            if (app == ArrApp.Radarr)
            {
                item["movieId"] = f.MediaId;
            }
            else
            {
                item["seriesId"] = f.MediaId;
                item["episodeIds"] = new JsonArray(f.EpisodeIds.Select(id => (JsonNode)id).ToArray());
                if (!string.IsNullOrEmpty(f.ReleaseType))
                {
                    item["releaseType"] = f.ReleaseType;
                }
            }

            list.Add(item);
        }

        string mode = importMode is "move" or "copy" ? importMode : "auto";
        var command = new JsonObject { ["name"] = "ManualImport", ["importMode"] = mode, ["files"] = list };
        using var result = await _arr.PostRawAsync(app, "command", command.ToJsonString(), ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return (false, result.Message);
        }

        _store.Record(new ActionRecord
        {
            Utc = DateTime.UtcNow,
            App = Key(app),
            Action = "import",
            Title = title,
            Detail = string.Format(CultureInfo.InvariantCulture, "{0} file{1}, {2}", files.Count, files.Count == 1 ? string.Empty : "s", mode),
            By = by
        });
        return (true, ArrClient.Name(app) + " is importing it now. It'll leave the queue once it's done.");
    }

    // ---------- Helpers ----------

    public static string Key(ArrApp app) => app == ArrApp.Sonarr ? "sonarr" : "radarr";

    private static EpisodeChoice EpisodeFrom(JsonElement e) => new()
    {
        Id = Json.Int(e, "id") ?? 0,
        SeasonNumber = Json.Int(e, "seasonNumber") ?? 0,
        EpisodeNumber = Json.Int(e, "episodeNumber") ?? 0,
        Title = Json.Text(e, "title"),
        HasFile = e.TryGetProperty("hasFile", out var h) && h.ValueKind == JsonValueKind.True
    };

    private static string? QualityName(JsonElement e) =>
        e.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object
        && q.TryGetProperty("quality", out var inner) && inner.ValueKind == JsonValueKind.Object
            ? Json.Text(inner, "name")
            : null;

    private static string WithYear(string? title, int? year) =>
        (title ?? "Untitled") + (year is > 0 ? " (" + year.Value.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty);

    private static string Simplify(string text) =>
        new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>Forgiving JSON readers: Sonarr and Radarr versions differ, so a missing field is never an error.</summary>
internal static class Json
{
    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (int)d : null;

    public static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (long)d : 0;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTime? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}

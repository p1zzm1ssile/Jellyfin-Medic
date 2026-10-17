using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Medic.Shared;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>One indexer as Sonarr or Radarr sees it.</summary>
public sealed class IndexerRow
{
    public string App { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty;

    // "ok", "failing" (paused after errors) or "off" (not used for RSS or searches).
    public string State { get; set; } = "ok";

    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Indexer status from Sonarr and Radarr: which indexers are failing, which are switched off, and the
/// indexer warnings from each app's health check. Indexers added by Prowlarr show here too, as
/// Prowlarr syncs them into Sonarr and Radarr.
/// </summary>
public class IndexersService
{
    private readonly ArrClient _arr;

    public IndexersService(ArrClient arr) => _arr = arr;

    public async Task<(List<IndexerRow> Rows, List<string> Warnings, Dictionary<string, string> Errors)> StatusAsync(CancellationToken ct)
    {
        var rows = new List<IndexerRow>();
        var warnings = new List<string>();
        var errors = new Dictionary<string, string>();
        foreach (var app in new[] { ArrApp.Sonarr, ArrApp.Radarr })
        {
            if (!_arr.IsConfigured(app))
            {
                continue;
            }

            string name = ArrClient.Name(app);
            using var indexers = await _arr.GetAsync(app, "indexer", ct).ConfigureAwait(false);
            if (!indexers.Ok || indexers.Json?.RootElement.ValueKind != JsonValueKind.Array)
            {
                errors[name.ToLowerInvariant()] = indexers.Ok ? DownloadsService.Unexpected(app, indexers, JsonValueKind.Array) : indexers.Message;
                continue;
            }

            // Sonarr and Radarr have no indexer status route (only Prowlarr does), so failing indexers come
            // from the two health checks that list them. Their messages are translated, so only the names
            // in them are read; an error names none and means every indexer in use is failing.
            var names = indexers.Json.RootElement.EnumerateArray().Select(i => Json.Text(i, "name") ?? string.Empty).ToList();
            var failing = new Dictionary<string, bool>(StringComparer.Ordinal); // name, and whether for more than 6 hours
            bool? allFailingLongTerm = null;
            using var health = await _arr.GetAsync(app, "health", ct).ConfigureAwait(false);
            if (health.Ok && health.Json?.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in health.Json.RootElement.EnumerateArray())
                {
                    string source = Json.Text(h, "source") ?? string.Empty;
                    string message = Json.Text(h, "message") ?? string.Empty;
                    if (source.Contains("Indexer", StringComparison.OrdinalIgnoreCase) && message.Length > 0)
                    {
                        warnings.Add(name + ": " + message);
                    }

                    bool longTerm = source.Equals("IndexerLongTermStatusCheck", StringComparison.OrdinalIgnoreCase);
                    if (!longTerm && !source.Equals("IndexerStatusCheck", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(Json.Text(h, "type"), "error", StringComparison.OrdinalIgnoreCase))
                    {
                        allFailingLongTerm = longTerm || allFailingLongTerm == true;
                    }
                    else
                    {
                        foreach (string listed in NamesListedIn(message, names))
                        {
                            failing[listed] = longTerm || failing.GetValueOrDefault(listed);
                        }
                    }
                }
            }

            foreach (var i in indexers.Json.RootElement.EnumerateArray())
            {
                var row = new IndexerRow
                {
                    App = name.ToLowerInvariant(),
                    Name = Json.Text(i, "name") ?? "Indexer",
                    Protocol = Json.Text(i, "protocol") ?? string.Empty
                };

                bool used = Json.Bool(i, "enableRss") || Json.Bool(i, "enableAutomaticSearch") || Json.Bool(i, "enableInteractiveSearch");
                if (used && (allFailingLongTerm is not null || failing.ContainsKey(row.Name)))
                {
                    bool longTerm = allFailingLongTerm == true || failing.GetValueOrDefault(row.Name);
                    row.State = "failing";
                    row.Detail = $"{name} has paused it after failures" + (longTerm ? " for more than 6 hours." : ".");
                }
                else if (!used)
                {
                    row.State = "off";
                    row.Detail = "Not used for RSS or searches.";
                }
                else
                {
                    row.Detail = string.Join(", ", new[] { Json.Bool(i, "enableRss") ? "RSS" : null, Json.Bool(i, "enableAutomaticSearch") ? "automatic search" : null, Json.Bool(i, "enableInteractiveSearch") ? "manual search" : null }.Where(x => x is not null));
                }

                rows.Add(row);
            }
        }

        return (rows.OrderBy(r => r.State == "failing" ? 0 : r.State == "off" ? 2 : 1).ThenBy(r => r.App).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(), warnings, errors);
    }

    /// <summary>
    /// The indexer names a health message lists. Sonarr and Radarr put them last, joined by ", ", after a
    /// sentence in the interface's language (Finnish adds a full stop). A name only counts when ", " or the
    /// end of the message follows it, and longer names are matched first, so "Geek" isn't found in "NZB Geek".
    /// </summary>
    private static List<string> NamesListedIn(string message, IReadOnlyCollection<string> names)
    {
        string text = message.TrimEnd();
        var taken = new bool[text.Length];
        var listed = new List<string>();
        foreach (string candidate in names.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(n => n.Length))
        {
            for (int at = text.IndexOf(candidate, StringComparison.Ordinal); at >= 0; at = text.IndexOf(candidate, at + 1, StringComparison.Ordinal))
            {
                string rest = text[(at + candidate.Length)..];
                bool endsThere = rest.Length == 0 || rest == "." || rest.StartsWith(", ", StringComparison.Ordinal);
                if (endsThere && !taken.AsSpan(at, candidate.Length).Contains(true))
                {
                    Array.Fill(taken, true, at, candidate.Length);
                    listed.Add(candidate);
                    break;
                }
            }
        }

        return listed;
    }
}

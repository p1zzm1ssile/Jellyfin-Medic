using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
/// Indexer status from Sonarr and Radarr: which indexers are failing (and paused until when), which
/// are switched off, and the indexer warnings from each app's health check. Indexers added by
/// Prowlarr show here too, as Prowlarr syncs them into Sonarr and Radarr.
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
                errors[name.ToLowerInvariant()] = indexers.Ok ? name + " sent something unexpected." : indexers.Message;
                continue;
            }

            // Failing indexers: Sonarr and Radarr pause them for longer after each failure.
            var status = new Dictionary<int, (DateTime? Since, DateTime? Till)>();
            using (var st = await _arr.GetAsync(app, "indexerstatus", ct).ConfigureAwait(false))
            {
                if (st.Ok && st.Json?.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in st.Json.RootElement.EnumerateArray())
                    {
                        if (Json.Int(s, "indexerId") is { } id)
                        {
                            status[id] = (Json.Date(s, "initialFailure") ?? Json.Date(s, "mostRecentFailure"), Json.Date(s, "disabledTill"));
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
                if (Json.Int(i, "id") is { } id && status.TryGetValue(id, out var s) && (s.Till is null || s.Till > DateTime.UtcNow))
                {
                    row.State = "failing";
                    row.Detail = "Failing" + (s.Since is { } since ? " since " + since.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
                        + (s.Till is { } till ? $". {name} won't use it until " + till.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "." : ".");
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

            using var health = await _arr.GetAsync(app, "health", ct).ConfigureAwait(false);
            if (health.Ok && health.Json?.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in health.Json.RootElement.EnumerateArray())
                {
                    if ((Json.Text(h, "source") ?? string.Empty).Contains("Indexer", StringComparison.OrdinalIgnoreCase) && Json.Text(h, "message") is { Length: > 0 } message)
                    {
                        warnings.Add(name + ": " + message);
                    }
                }
            }
        }

        return (rows.OrderBy(r => r.State == "failing" ? 0 : r.State == "off" ? 2 : 1).ThenBy(r => r.App).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(), warnings, errors);
    }
}

using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;

namespace JellyfinMedic.Services;

/// <summary>One banner for admins on the Jellyfin home page.</summary>
public class AdminAlert
{
    // Changes when there's something new to say, so a dismissed banner comes back for a new problem.
    public string Id { get; set; } = string.Empty;

    // "critical" or "restart".
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Serious problems worth telling an admin about straight away, however often they've happened:
/// a damaged database, a full disk, a failed database upgrade, a plugin that couldn't load, or a
/// fatal error; and Jellyfin waiting for a restart to finish installing updates.
/// Reads only the end of the newest log files, and keeps the result for 2 minutes.
/// </summary>
public static class AdminAlerts
{
    private const long TailBytes = 8L * 1024 * 1024;
    private static readonly object Sync = new();
    private static (DateTime At, List<AdminAlert> Result)? _cache;

    private static readonly (string Key, string Title, string Advice, Regex Pattern)[] Critical =
    {
        ("database", "Jellyfin's database looks damaged",
            "Stop Jellyfin and restore jellyfin.db from a backup before it gets worse. Medic → Checks has more.",
            new Regex(@"database disk image is malformed|SQLite Error 11\b|file is not a database", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("disk", "A disk Jellyfin uses is full",
            "Free some space (Medic → Dashboard → Free up space can help), or Jellyfin may stop saving data.",
            new Regex(@"No space left on device|There is not enough space on the disk|disk (is )?full", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("migration", "A Jellyfin database upgrade failed",
            "Jellyfin may not work properly until this is fixed. Check the log, and restore a backup if needed.",
            new Regex(@"migration\b.*\b(failed|error)|error (while )?(running|applying) migration", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("plugin", "A plugin couldn't be loaded",
            "Update or remove the plugin under Dashboard → Plugins. Medic → Checks names it.",
            new Regex(@"Failed to load assembly|Disabling plugin|plugin .* (malfunctioned|failed to (load|initiali[sz]e))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("fatal", "Jellyfin hit a fatal error",
            "Something stopped Jellyfin working properly. The Jellyfin log has the details.",
            new Regex(@"^\[[^\]]+\]\s*\[FTL\]", RegexOptions.Compiled))
    };

    private static readonly Regex Stamp = new(@"^\[(\d{4}-\d{2}-\d{2})?[ T]?(\d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    // The level of a log entry, e.g. [ERR]. Lines without one (exception text) belong to the entry above.
    private static readonly Regex Level = new(@"^\[[^\]]+\]\s*\[([A-Z]{3})\]", RegexOptions.Compiled);

    public static List<AdminAlert> Current(string logDirectory, IServerApplicationHost host, PluginConfiguration cfg)
    {
        var alerts = new List<AdminAlert>();

        if (cfg.AlertCriticalErrors)
        {
            alerts.AddRange(CriticalFromLogs(logDirectory));
        }

        if (cfg.AlertRestartNeeded && host.HasPendingRestart)
        {
            alerts.Add(new AdminAlert
            {
                // Once per server start: after the restart, a new update brings it back.
                Id = "restart:" + System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                Kind = "restart",
                Title = "Jellyfin needs a restart",
                Detail = "Updated plugins finish installing when Jellyfin restarts. Restart it from Dashboard → Restart when nobody's watching, or let Medic's \"Restart Jellyfin for waiting updates\" task do it overnight."
            });
        }

        return alerts;
    }

    /// <summary>Serious errors in the newest logs, for the banners and for Checks.</summary>
    public static List<AdminAlert> CriticalFromLogs(string logDirectory)
    {
        lock (Sync)
        {
            if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2))
            {
                return c.Result;
            }
        }

        var found = new Dictionary<string, (string Line, string When)>();
        try
        {
            // Today's and yesterday's logs, newest last so the latest sighting wins.
            var files = new DirectoryInfo(logDirectory).GetFiles("*.log")
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-2))
                .OrderBy(f => f.LastWriteTimeUtc)
                .TakeLast(3);
            foreach (var file in files)
            {
                // Only errors and fatal errors count, with the exception text printed under them. Warnings
                // often mention these words in passing ("may indicate ... database corruption", a migration
                // step that "cannot be executed in a transaction") without anything being wrong.
                bool serious = false;
                string day = file.LastWriteTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var line in Tail(file.FullName))
                {
                    var level = Level.Match(line);
                    if (level.Success)
                    {
                        serious = level.Groups[1].Value is "ERR" or "FTL";
                        var stamp = Stamp.Match(line);
                        if (stamp.Success && stamp.Groups[1].Success)
                        {
                            day = stamp.Groups[1].Value;
                        }
                    }

                    if (!serious)
                    {
                        continue;
                    }

                    foreach (var (key, _, _, pattern) in Critical)
                    {
                        if (pattern.IsMatch(line))
                        {
                            found[key] = (line, day);
                        }
                    }
                }
            }
        }
        catch
        {
            // No readable log: nothing to report.
        }

        var result = Critical
            .Where(c => found.ContainsKey(c.Key))
            .Select(c => new AdminAlert
            {
                // Once a day per kind of problem, so a dismissed banner returns if it happens again tomorrow.
                Id = "critical:" + c.Key + ":" + found[c.Key].When,
                Kind = "critical",
                Title = c.Title,
                Detail = c.Advice + " Last seen: " + Shorten(found[c.Key].Line)
            })
            .ToList();

        lock (Sync)
        {
            _cache = (DateTime.UtcNow, result);
        }

        return result;
    }

    private static IEnumerable<string> Tail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > TailBytes)
        {
            stream.Seek(-TailBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (stream.Position > 0)
        {
            reader.ReadLine(); // probably a partial line
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static string Shorten(string line)
    {
        string text = line.Trim();
        return text.Length > 220 ? text[..220] + "…" : text;
    }

    /// <summary>Clears the cache, for tests and after settings change.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _cache = null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

public class RepeatedError
{
    public string Source { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public int Count { get; set; }

    // When it last appeared, in the server's local time.
    public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>
/// Finds errors that keep repeating in today's Jellyfin log, such as an app stuck retrying a
/// request it can never get. Reads only the end of each of today's log files, and keeps the
/// result for 5 minutes.
/// </summary>
public static class LogScanner
{
    private const long MaxBytesPerFile = 30L * 1024 * 1024;
    private static readonly object Sync = new();
    private static (DateTime At, List<RepeatedError> Result)? _cache;

    private static readonly Regex ErrorLine = new(@"^\[([^\]]+)\]\s*\[ERR\]\s*\[[^\]]*\]\s*([A-Za-z0-9_.]+):\s*(.*)$", RegexOptions.Compiled);

    // "Fixed it, count again from now": errors logged before this moment are left out.
    private static DateTimeOffset? _countFrom;
    private static bool _countFromLoaded;

    private static string CountFromFile(string stateDir) => Path.Combine(stateDir, "JellyfinMedic", "log_count_from.txt");

    /// <summary>Starts counting repeated errors afresh from now (after you've fixed the cause).</summary>
    public static void ResetCount(string stateDir)
    {
        lock (Sync)
        {
            _countFrom = DateTimeOffset.Now;
            _countFromLoaded = true;
            _cache = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(stateDir, "JellyfinMedic"));
                File.WriteAllText(CountFromFile(stateDir), _countFrom.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                // Kept in memory until the next restart.
            }
        }
    }

    /// <summary>The moment counting restarted today, if it did.</summary>
    public static DateTimeOffset? CountingFrom(string stateDir)
    {
        lock (Sync)
        {
            if (!_countFromLoaded)
            {
                _countFromLoaded = true;
                try
                {
                    string file = CountFromFile(stateDir);
                    if (File.Exists(file) && DateTimeOffset.TryParse(File.ReadAllText(file).Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at))
                    {
                        _countFrom = at;
                    }
                }
                catch
                {
                    // No saved point: count the whole day.
                }
            }

            return _countFrom is { } from && from.LocalDateTime.Date == DateTime.Now.Date ? from : null;
        }
    }
    private static readonly Regex ExceptionLine = new(@"^\s*([A-Za-z0-9_.]+(?:Exception|Error))(?:\s*\([^)]*\))?:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Ids = new(@"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}|[0-9a-fA-F]{24,}", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex Query = new(@"\?[^\s""']*", RegexOptions.Compiled);

    public static List<RepeatedError> Scan(string logDirectory, string? stateDir = null)
    {
        // A log file written today can start yesterday, so entries before midnight are left out too.
        DateTimeOffset midnight = new DateTimeOffset(DateTime.Now.Date);
        DateTimeOffset from = (stateDir is null ? null : CountingFrom(stateDir)) ?? midnight;
        lock (Sync)
        {
            if (_cache is { } hit && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
            {
                return hit.Result;
            }
        }

        var counts = new Dictionary<string, RepeatedError>(StringComparer.Ordinal);
        try
        {
            var today = DateTime.Now.Date;
            var files = new DirectoryInfo(logDirectory).GetFiles("log_*.log")
                .Where(f => f.LastWriteTime.Date == today)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(3);

            foreach (var file in files)
            {
                ScanFile(file.FullName, counts, from);
            }
        }
        catch
        {
            // Log folder unreadable: nothing to report.
        }

        var result = counts.Values.Where(r => r.Count >= 50).OrderByDescending(r => r.Count).Take(10).ToList();
        lock (Sync)
        {
            _cache = (DateTime.UtcNow, result);
        }

        return result;
    }

    public static List<Finding> Findings(List<RepeatedError> repeated, DateTimeOffset? countingFrom = null) =>
        repeated.Where(r => r.Count >= 100).Take(5).Select(r => new Finding
        {
            Area = "Logs",
            Severity = r.Count >= 1000 ? Sev.Problem : Sev.Improve,
            Title = countingFrom is { } since
                ? $"An error has repeated {r.Count:N0} times since {since.LocalDateTime:HH:mm}"
                : $"An error has repeated {r.Count:N0} times today",
            Current = $"{r.Source}: {r.Message}" + (r.LastSeen is { } last ? $" (last at {last.LocalDateTime:HH:mm})" : string.Empty),
            Recommended = "Find what keeps causing it and stop it",
            Why = "The same error over and over usually means an app, browser page or plugin is stuck retrying something that can never work. Each attempt costs CPU and fills the log.",
            Where = "Dashboard → Logs (search for the message)"
        }).ToList();

    private static void ScanFile(string path, Dictionary<string, RepeatedError> counts, DateTimeOffset? from)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytesPerFile)
        {
            stream.Seek(-MaxBytesPerFile, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? pendingKey = null;
        (string Source, string Message, DateTimeOffset? At)? pending = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var err = ErrorLine.Match(line);
            if (err.Success)
            {
                Commit(counts, pending);
                DateTimeOffset? at = DateTimeOffset.TryParse(err.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
                if (from is { } start && at is { } stamp && stamp < start)
                {
                    pending = null; // before "count again from now"
                    pendingKey = null;
                    continue;
                }

                pending = (err.Groups[2].Value, Normalise(err.Groups[3].Value), at);
                pendingKey = "open";
                continue;
            }

            // The line after an error often names the exception: add it so different causes stay apart.
            if (pendingKey == "open" && pending is { } p)
            {
                var ex = ExceptionLine.Match(line);
                if (ex.Success)
                {
                    pending = (p.Source, p.Message + " → " + ex.Groups[1].Value + ": " + Normalise(ex.Groups[2].Value), p.At);
                }

                pendingKey = null;
            }
        }

        Commit(counts, pending);
    }

    private static void Commit(Dictionary<string, RepeatedError> counts, (string Source, string Message, DateTimeOffset? At)? entry)
    {
        if (entry is not { } e)
        {
            return;
        }

        string key = e.Source + "|" + e.Message;
        if (!counts.TryGetValue(key, out var item))
        {
            item = new RepeatedError { Source = e.Source, Message = e.Message };
            counts[key] = item;
        }

        item.Count++;
        if (e.At is { } at && (item.LastSeen is null || at > item.LastSeen))
        {
            item.LastSeen = at;
        }
    }

    private static string Normalise(string message)
    {
        string text = Query.Replace(message, string.Empty);
        text = Ids.Replace(text, "…");
        text = Numbers.Replace(text, "#");
        text = SettingsReader.MaskSecrets(text).Trim();
        return text.Length > 600 ? text[..600] + "…" : text;
    }
}

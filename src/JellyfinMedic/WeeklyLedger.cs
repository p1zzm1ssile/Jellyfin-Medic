using System.Globalization;
using System.Text.Json;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

public sealed class WeeklySummary
{
    public DateTime Since { get; set; }

    public int NeedChanging { get; set; }

    public int CouldBeBetter { get; set; }

    public List<string> NewIssues { get; set; } = new();

    public List<string> FixedIssues { get; set; } = new();

    public List<string> IgnoredIssues { get; set; } = new();

    public long FreedTotal { get; set; }

    public Dictionary<string, long> FreedBy { get; set; } = new();

    public int Restarts { get; set; }
}

/// <summary>
/// A small record of the last few weeks, for the Dashboard's "This week" tile: which issues appeared
/// or went away, how much space Medic freed, and how often it restarted Jellyfin. Counts and titles only.
/// </summary>
public static class WeeklyLedger
{
    private static readonly object Sync = new();
    private static string? _file;
    private static Ledger _ledger = new();

    internal sealed class DaySnapshot
    {
        public DateTime Day { get; set; }

        public Dictionary<string, string> Issues { get; set; } = new(); // key → title

        // Keys of the findings ignored that day. Snapshots saved before this was recorded read as none.
        public List<string> Ignored { get; set; } = new();

        public int Problems { get; set; }

        public int Others { get; set; }
    }

    private sealed class Freed
    {
        public DateTime At { get; set; }

        public string Kind { get; set; } = string.Empty;

        public long Bytes { get; set; }
    }

    private sealed class Ledger
    {
        public List<DaySnapshot> Days { get; set; } = new();

        public List<Freed> Freed { get; set; } = new();

        public List<DateTime> Restarts { get; set; } = new();
    }

    public static void Init(string pluginConfigurationsPath)
    {
        lock (Sync)
        {
            if (_file is not null)
            {
                return;
            }

            _file = Path.Combine(pluginConfigurationsPath, "JellyfinMedic", "weekly.json");
            try
            {
                if (File.Exists(_file))
                {
                    _ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(_file)) ?? new Ledger();
                }
            }
            catch
            {
                _ledger = new Ledger();
            }
        }
    }

    /// <summary>Records today's open issues (one snapshot per day, the latest wins).</summary>
    public static void Snapshot(IEnumerable<Finding> findings)
    {
        var day = BuildDay(findings, DateTime.Now.Date);
        lock (Sync)
        {
            _ledger.Days.RemoveAll(d => d.Day == day.Day || d.Day < day.Day.AddDays(-15));
            _ledger.Days.Add(day);
            Save();
        }
    }

    /// <summary>One day's record from a report's findings: open issues by key and title, and the keys of ignored ones.</summary>
    internal static DaySnapshot BuildDay(IEnumerable<Finding> findings, DateTime day)
    {
        var counted = findings.Where(f => f.Severity is Sev.Problem or Sev.Improve or Sev.Tip).ToList();
        var open = counted.Where(f => !f.Ignored).ToList();
        return new DaySnapshot
        {
            Day = day,
            Issues = open.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First().Title),
            Ignored = counted.Where(f => f.Ignored).Select(KeyOf).Distinct().ToList(),
            Problems = open.Count(f => f.Severity == Sev.Problem),
            Others = open.Count(f => f.Severity != Sev.Problem)
        };
    }

    public static void AddFreed(string kind, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (Sync)
        {
            _ledger.Freed.RemoveAll(f => f.At < DateTime.Now.AddDays(-60));
            _ledger.Freed.Add(new Freed { At = DateTime.Now, Kind = kind, Bytes = bytes });
            Save();
        }
    }

    public static void AddRestart()
    {
        lock (Sync)
        {
            _ledger.Restarts.RemoveAll(r => r < DateTime.Now.AddDays(-60));
            _ledger.Restarts.Add(DateTime.Now);
            Save();
        }
    }

    /// <summary>The days Medic freed space since a date (server local), oldest first, with the bytes and kinds of each.</summary>
    public static List<FreedDay> FreedDays(DateTime since)
    {
        lock (Sync)
        {
            return _ledger.Freed
                .Where(f => f.At >= since)
                .GroupBy(f => f.At.Date)
                .OrderBy(g => g.Key)
                .Select(g => new FreedDay
                {
                    Day = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Bytes = g.Sum(f => f.Bytes),
                    Kinds = g.Select(f => f.Kind).Distinct(StringComparer.Ordinal).ToList()
                })
                .ToList();
        }
    }

    public static WeeklySummary Summary()
    {
        lock (Sync)
        {
            var weekAgo = DateTime.Now.Date.AddDays(-7);
            var now = _ledger.Days.OrderByDescending(d => d.Day).FirstOrDefault();
            // Compare with the snapshot from a week ago, or the oldest one we have.
            var then = _ledger.Days.Where(d => d.Day <= weekAgo).OrderByDescending(d => d.Day).FirstOrDefault()
                       ?? _ledger.Days.OrderBy(d => d.Day).FirstOrDefault();

            var summary = new WeeklySummary { Since = then?.Day ?? DateTime.Now.Date };
            if (now is not null)
            {
                summary.NeedChanging = now.Problems;
                summary.CouldBeBetter = now.Others;
                if (then is not null && then.Day < now.Day)
                {
                    Compare(then, now, summary);
                }
            }

            var freed = _ledger.Freed.Where(f => f.At >= summary.Since).ToList();
            summary.FreedTotal = freed.Sum(f => f.Bytes);
            summary.FreedBy = freed.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Sum(f => f.Bytes));
            summary.Restarts = _ledger.Restarts.Count(r => r >= summary.Since);
            return summary;
        }
    }

    /// <summary>
    /// What changed between two days: new issues, fixed ones, and ones ignored since. An issue ignored since
    /// isn't fixed, and one shown again after being ignored isn't new.
    /// </summary>
    internal static void Compare(DaySnapshot then, DaySnapshot now, WeeklySummary summary)
    {
        var ignoredNow = new HashSet<string>(now.Ignored);
        var ignoredThen = new HashSet<string>(then.Ignored);
        summary.NewIssues = now.Issues.Where(i => !then.Issues.ContainsKey(i.Key) && !ignoredThen.Contains(i.Key)).Select(i => i.Value).Take(10).ToList();
        summary.FixedIssues = then.Issues.Where(i => !now.Issues.ContainsKey(i.Key) && !ignoredNow.Contains(i.Key)).Select(i => i.Value).Take(10).ToList();
        summary.IgnoredIssues = then.Issues.Where(i => ignoredNow.Contains(i.Key)).Select(i => i.Value).Take(10).ToList();
    }

    private static string KeyOf(Finding f) => string.IsNullOrEmpty(f.Key) ? IgnoreStore.KeyFor(f) : f.Key;

    private static void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            // Written to a temporary file first, so a crash mid-write can't wipe the history.
            ScheduleStorage.WriteText(_file, JsonSerializer.Serialize(_ledger));
        }
        catch
        {
            // Kept in memory until the next restart.
        }
    }
}

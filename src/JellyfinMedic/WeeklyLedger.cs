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

    private sealed class DaySnapshot
    {
        public DateTime Day { get; set; }

        public Dictionary<string, string> Issues { get; set; } = new(); // key → title

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
        var open = findings.Where(f => !f.Ignored && f.Severity is Sev.Problem or Sev.Improve or Sev.Tip).ToList();
        lock (Sync)
        {
            var today = DateTime.Now.Date;
            _ledger.Days.RemoveAll(d => d.Day == today || d.Day < today.AddDays(-15));
            _ledger.Days.Add(new DaySnapshot
            {
                Day = today,
                Issues = open.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First().Title),
                Problems = open.Count(f => f.Severity == Sev.Problem),
                Others = open.Count(f => f.Severity != Sev.Problem)
            });
            Save();
        }
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
                    summary.NewIssues = now.Issues.Where(i => !then.Issues.ContainsKey(i.Key)).Select(i => i.Value).Take(10).ToList();
                    summary.FixedIssues = then.Issues.Where(i => !now.Issues.ContainsKey(i.Key)).Select(i => i.Value).Take(10).ToList();
                }
            }

            var freed = _ledger.Freed.Where(f => f.At >= summary.Since).ToList();
            summary.FreedTotal = freed.Sum(f => f.Bytes);
            summary.FreedBy = freed.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Sum(f => f.Bytes));
            summary.Restarts = _ledger.Restarts.Count(r => r >= summary.Since);
            return summary;
        }
    }

    private static string KeyOf(Finding f) => string.IsNullOrEmpty(f.Key) ? f.Area + "|" + f.Title : f.Key;

    private static void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            // Written to a temporary file first, so a crash mid-write can't wipe the history.
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_ledger));
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // Kept in memory until the next restart.
        }
    }
}

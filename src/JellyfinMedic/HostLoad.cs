using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Every 5 minutes, reads how busy the whole server is: CPU, memory and disk for the machine as a
/// whole, from the totals Linux shows every program in /proc. It never looks at single processes,
/// containers or apps, so Medic learns how busy the server usually is, not what makes it busy.
/// The readings go into an hour-of-the-week profile that the planner uses next to the viewing
/// pattern. Readings whose 5 minutes overlap one of Jellyfin's scheduled tasks are left out, as those
/// are the tasks the planner moves; counting them would push a task away from wherever it runs now.
/// Nothing is read on Windows or macOS.
/// </summary>
public sealed class HostLoadSampler : IHostedService, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly ITaskManager _tasks;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<HostLoadSampler> _logger;
    private Timer? _timer;
    private HostCounters? _previous;

    // Whether the last reading failed, so a repeated failure is a warning once.
    private bool _failing;

    public HostLoadSampler(ITaskManager tasks, IApplicationPaths paths, ILogger<HostLoadSampler> logger)
    {
        _tasks = tasks;
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            // The first reading only sets the starting point for the CPU and disk counters.
            _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Every);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Whether a scheduled task ran at any time since <paramref name="sinceUtc"/>: one running now, or one whose last
    /// run ended since then. A running task's last result is its previous run, so its state is what tells.
    /// </summary>
    internal static bool TaskTouched(IEnumerable<IScheduledTaskWorker> tasks, DateTime sinceUtc) =>
        tasks.Any(t => t.State == TaskState.Running || t.LastExecutionResult?.EndTimeUtc >= sinceUtc);

    private void Sample()
    {
        try
        {
            var now = HostCounters.Read(DateTime.UtcNow);
            var previous = _previous;
            _previous = now;
            // The reading covers the whole spell since the previous one, so a task that ended during it left its load there too.
            if (previous is null || TaskTouched(_tasks.ScheduledTasks, previous.TimeUtc))
            {
                return;
            }

            if (HostCounters.Between(previous, now) is { } reading)
            {
                HostLoadStore.Record(_paths, DateTime.Now, reading);
            }
        }
        catch (Exception ex)
        {
            // A warning the first time, as a failure here repeats on every reading.
            if (!_failing)
            {
                _failing = true;
                _logger.LogWarning(ex, "Jellyfin Medic: couldn't record the server's load; Medic keeps trying.");
            }
            else
            {
                _logger.LogDebug(ex, "Jellyfin Medic: still couldn't record the server's load.");
            }

            return;
        }

        if (_failing)
        {
            _failing = false;
            _logger.LogInformation("Jellyfin Medic: recording the server's load works again.");
        }
    }
}

/// <summary>How busy the whole server was over one spell, as shares of 0 to 100.</summary>
public sealed record HostLoadReading(double? CpuPercent, double? MemoryPercent, double? DiskBusyPercent);

/// <summary>
/// The machine-wide counters behind a reading: CPU time from /proc/stat, memory from /proc/meminfo,
/// and time spent on disk I/O from /proc/diskstats (whole drives only, added up, never stored by drive).
/// </summary>
public sealed class HostCounters
{
    public DateTime TimeUtc { get; init; }

    public long? CpuBusy { get; init; }

    public long? CpuTotal { get; init; }

    public double? MemoryPercent { get; init; }

    // Milliseconds each whole drive spent doing I/O; only the busiest drive's share is kept.
    public Dictionary<string, long> DiskIoMs { get; init; } = new();

    public static HostCounters Read(DateTime utcNow)
    {
        var cpu = ParseCpu(HostPlatform.Lines("/proc/stat"));
        return new HostCounters
        {
            TimeUtc = utcNow,
            CpuBusy = cpu?.Busy,
            CpuTotal = cpu?.Total,
            MemoryPercent = ParseMemory(HostPlatform.Lines("/proc/meminfo"), HostPlatform.Lines(ArcStatsPath), HostPlatform.Kind),
            DiskIoMs = ParseDisks(HostPlatform.Lines("/proc/diskstats"), name => Directory.Exists("/sys/block/" + name))
        };
    }

    /// <summary>The reading over the spell between two sets of counters, or null if nothing could be read.</summary>
    public static HostLoadReading? Between(HostCounters before, HostCounters after)
    {
        double wallMs = (after.TimeUtc - before.TimeUtc).TotalMilliseconds;
        if (wallMs <= 0)
        {
            return null;
        }

        double? cpu = null;
        if (before.CpuTotal is { } t0 && after.CpuTotal is { } t1 && t1 > t0 && before.CpuBusy is { } b0 && after.CpuBusy is { } b1)
        {
            cpu = Math.Round(Math.Clamp((b1 - b0) * 100.0 / (t1 - t0), 0, 100), 1);
        }

        double? disk = null;
        foreach (var (name, ms) in after.DiskIoMs)
        {
            if (before.DiskIoMs.TryGetValue(name, out long earlier) && ms >= earlier)
            {
                double busy = Math.Clamp((ms - earlier) * 100.0 / wallMs, 0, 100);
                disk = Math.Round(Math.Max(disk ?? 0, busy), 1);
            }
        }

        var reading = new HostLoadReading(cpu, after.MemoryPercent, disk);
        return reading.CpuPercent is null && reading.MemoryPercent is null && reading.DiskBusyPercent is null ? null : reading;
    }

    /// <summary>All CPU time and the part of it not idle, from the "cpu" line of /proc/stat.</summary>
    internal static (long Busy, long Total)? ParseCpu(IEnumerable<string> lines)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        // user nice system idle iowait irq softirq steal (guest time is already in user and nice).
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(f => long.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0).ToArray();
        if (fields.Length < 4)
        {
            return null;
        }

        long total = fields.Sum();
        long idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
        return (total - idle, total);
    }

    internal const string ArcStatsPath = "/proc/spl/kstat/zfs/arcstats";

    /// <summary>
    /// Share of memory in use, not counting what can be freed at once: the file cache, and ZFS's cache (ARC) above its
    /// minimum, which MemAvailable leaves out though ZFS gives it back when programs need memory.
    /// </summary>
    internal static double? ParseMemory(IEnumerable<string> meminfo, IEnumerable<string>? arcstats = null, HostKind kind = HostKind.Linux) =>
        MemoryKb(meminfo, arcstats, kind) is { } kb ? UsedPercent(kb) : null;

    /// <summary>Total memory and what can be used at once, in kB, counted as ParseMemory counts them.</summary>
    internal static (long Total, long Available)? MemoryKb(IEnumerable<string> meminfo, IEnumerable<string>? arcstats = null, HostKind kind = HostKind.Linux)
    {
        long? total = null, available = null;
        foreach (var line in meminfo)
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = Number(line, 1);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = Number(line, 1);
            }
        }

        if (total is not > 0 || available is not { } free)
        {
            return null;
        }

        // Inside an LXC, lxcfs shows the container's own memory in /proc/meminfo, but arcstats is the host's ARC, which
        // the container can't get back. A Docker container's /proc/meminfo is the host's, so the ARC matches it there.
        // arcstats lines are "name type value", in bytes.
        long? arcSize = null, arcMin = null;
        var arcLines = kind == HostKind.ProxmoxLxc ? null : arcstats;
        foreach (var line in arcLines ?? Enumerable.Empty<string>())
        {
            if (line.StartsWith("size ", StringComparison.Ordinal))
            {
                arcSize = Number(line, 2);
            }
            else if (line.StartsWith("c_min ", StringComparison.Ordinal))
            {
                arcMin = Number(line, 2);
            }
        }

        long arcFreeable = arcSize is { } size && arcMin is { } min ? Math.Max(0, size - min) / 1024 : 0;
        return (total.Value, Math.Min(total.Value, free + arcFreeable));

        static long? Number(string line, int field) =>
            long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    internal static double UsedPercent((long Total, long Available) kb) =>
        Math.Round(Math.Clamp((kb.Total - kb.Available) * 100.0 / kb.Total, 0, 100), 1);

    /// <summary>
    /// Milliseconds spent on I/O for each whole drive in /proc/diskstats. Partitions would count a drive
    /// twice, and loop, RAM and device-mapper devices sit on top of real drives, so they're left out.
    /// </summary>
    internal static Dictionary<string, long> ParseDisks(IEnumerable<string> lines, Func<string, bool> isWholeDrive)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // major minor name, then 11 or more counters; the 10th counter is time spent doing I/O.
            if (f.Length < 13)
            {
                continue;
            }

            string name = f[2];
            if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)
                || name.StartsWith("dm-", StringComparison.Ordinal) || name.StartsWith("zram", StringComparison.Ordinal)
                || name.StartsWith("md", StringComparison.Ordinal) || !isWholeDrive(name))
            {
                continue;
            }

            if (long.TryParse(f[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
            {
                result[name] = ms;
            }
        }

        return result;
    }
}

/// <summary>The whole server's memory in use now, counted as the load profile counts it. Linux only.</summary>
public static class ServerMemory
{
    /// <summary>Percent in use, with used and total GB; null off Linux or when /proc/meminfo can't be read.</summary>
    public static double? UsedPercent(out double usedGb, out double totalGb)
    {
        usedGb = 0;
        totalGb = 0;
        if (!OperatingSystem.IsLinux() || HostCounters.MemoryKb(HostPlatform.Lines("/proc/meminfo"), HostPlatform.Lines(HostCounters.ArcStatsPath), HostPlatform.Kind) is not { } kb)
        {
            return null;
        }

        usedGb = Math.Round((kb.Total - kb.Available) / 1048576.0, 1);
        totalGb = Math.Round(kb.Total / 1048576.0, 1);
        return HostCounters.UsedPercent(kb);
    }
}

/// <summary>One hour of the week in the server load profile.</summary>
public class HostLoadBucket
{
    public int CpuSamples { get; set; }

    public double CpuSum { get; set; }

    public double CpuMax { get; set; }

    public int MemorySamples { get; set; }

    public double MemorySum { get; set; }

    public int DiskSamples { get; set; }

    public double DiskSum { get; set; }

    public double DiskMax { get; set; }
}

/// <summary>Server load by hour of the week (168 hours, Monday 00:00 first). Only totals, never what used them.</summary>
public class HostLoadProfile
{
    public List<HostLoadBucket> Buckets { get; set; } = new();

    public long TotalSamples { get; set; }

    public DateTime? FirstSampleUtc { get; set; }

    public DateTime? LastSampleUtc { get; set; }
}

public static class HostLoadStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FilePath(IApplicationPaths paths) =>
        Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "host_load_profile.json");

    public static HostLoadProfile Load(IApplicationPaths paths)
    {
        lock (Sync)
        {
            return LoadFrom(FilePath(paths), forReading: true);
        }
    }

    public static void Record(IApplicationPaths paths, DateTime localNow, HostLoadReading reading)
    {
        lock (Sync)
        {
            string path = FilePath(paths);
            var profile = LoadFrom(path, forReading: false);
            Add(profile, localNow, reading);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ScheduleStorage.WriteText(path, JsonSerializer.Serialize(profile, Indented));
        }
    }

    internal static void Add(HostLoadProfile profile, DateTime localNow, HostLoadReading reading)
    {
        Fill(profile);
        var bucket = profile.Buckets[(ScheduleStorage.MondayFirst(localNow.DayOfWeek) * 24) + localNow.Hour];
        if (reading.CpuPercent is { } cpu)
        {
            bucket.CpuSamples++;
            bucket.CpuSum += cpu;
            bucket.CpuMax = Math.Max(bucket.CpuMax, cpu);
        }

        if (reading.MemoryPercent is { } memory)
        {
            bucket.MemorySamples++;
            bucket.MemorySum += memory;
        }

        if (reading.DiskBusyPercent is { } disk)
        {
            bucket.DiskSamples++;
            bucket.DiskSum += disk;
            bucket.DiskMax = Math.Max(bucket.DiskMax, disk);
        }

        profile.TotalSamples++;
        profile.FirstSampleUtc ??= DateTime.UtcNow;
        profile.LastSampleUtc = DateTime.UtcNow;
    }

    private static HostLoadProfile LoadFrom(string path, bool forReading)
    {
        HostLoadProfile? profile = null;
        try
        {
            if (File.Exists(path))
            {
                profile = JsonSerializer.Deserialize<HostLoadProfile>(File.ReadAllText(path));
            }
        }
        catch (JsonException ex)
        {
            try
            {
                ScheduleStorage.SetAside(path, ex);
            }
            catch (Exception moveFailed) when (forReading && moveFailed is IOException or UnauthorizedAccessException)
            {
                // Reading only shows the profile, so the report, the usage page and the planner get an empty one and
                // carry on. Recording still fails, so nothing writes over the damaged file before it's set aside.
                ScheduleStorage.Logger?.LogWarning(moveFailed, "Jellyfin Medic: {File} couldn't be read or kept aside as .bad, so it reads as empty", Path.GetFileName(path));
            }

            profile = null;
        }

        profile ??= new HostLoadProfile();
        Fill(profile);
        return profile;
    }

    private static void Fill(HostLoadProfile profile)
    {
        while (profile.Buckets.Count < 168)
        {
            profile.Buckets.Add(new HostLoadBucket());
        }
    }
}

/// <summary>The server load profile in the shape the planner needs.</summary>
public class HostLoadSummary
{
    public double HoursCollected { get; set; }

    public bool Ready { get; set; }

    // Averages by hour of the week, Monday 00:00 first; null where that hour has no reading yet.
    public List<double?> AverageCpu { get; set; } = new();

    public List<double?> AverageMemory { get; set; } = new();

    public List<double?> AverageDisk { get; set; } = new();
}

public static class HostLoadAnalyzer
{
    // Memory only counts once it's this full: most servers sit at a steady share all day.
    private const double MemoryPressureFrom = 70;

    public static HostLoadSummary Summarise(HostLoadProfile profile)
    {
        var buckets = profile.Buckets.Count == 168 ? profile.Buckets : Enumerable.Range(0, 168).Select(_ => new HostLoadBucket()).ToList();
        var summary = new HostLoadSummary
        {
            // One reading every 5 minutes = 12 per hour.
            HoursCollected = profile.TotalSamples / 12.0,
            AverageCpu = buckets.Select(b => Average(b.CpuSum, b.CpuSamples)).ToList(),
            AverageMemory = buckets.Select(b => Average(b.MemorySum, b.MemorySamples)).ToList(),
            AverageDisk = buckets.Select(b => Average(b.DiskSum, b.DiskSamples)).ToList()
        };

        summary.Ready = summary.HoursCollected >= 72;
        return summary;
    }

    /// <summary>
    /// How busy the server usually is in each hour of the week, 0 (idle) to 1 (flat out): the CPU,
    /// the busiest drive, or memory past 70% full, whichever is highest. Null where nothing is known.
    /// </summary>
    public static double?[] Busyness(HostLoadSummary summary)
    {
        var result = new double?[168];
        for (int i = 0; i < 168; i++)
        {
            double? memory = summary.AverageMemory.ElementAtOrDefault(i) is { } m
                ? Math.Max(0, (m - MemoryPressureFrom) / (100 - MemoryPressureFrom) * 100)
                : null;
            var known = new[] { summary.AverageCpu.ElementAtOrDefault(i), summary.AverageDisk.ElementAtOrDefault(i), memory }
                .Where(v => v is not null).Select(v => v!.Value).ToList();
            result[i] = known.Count > 0 ? Math.Clamp(known.Max() / 100, 0, 1) : null;
        }

        return result;
    }

    private static double? Average(double sum, int samples) => samples == 0 ? null : Math.Round(sum / samples, 1);
}

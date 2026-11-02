using System.Diagnostics;
using System.Collections.Concurrent;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

public class NowSnapshot
{
    public double CpuPercent { get; set; }

    public List<double> CpuHistory { get; set; } = new();

    public double MemoryGb { get; set; }

    public double MemoryLimitGb { get; set; }

    // MemoryGb as a share of MemoryLimitGb, for the Dashboard's warning colours.
    public double MemoryPercent { get; set; }

    public int Watching { get; set; }

    public int Transcoding { get; set; }

    public int ActiveDevices { get; set; }

    public List<string> RunningTasks { get; set; } = new();

    // Jellyfin plus the FFmpeg processes it started, from the resource monitor. Null until its first reading.
    public ResourceSample? Resources { get; set; }
}

/// <summary>What Jellyfin is doing right now, for the Dashboard. Cheap: no database queries.</summary>
public static class ServerNow
{
    private static readonly object Sync = new();
    private static readonly Queue<double> History = new();
    private static readonly ConcurrentDictionary<string, DateTime> RunningSince = new(StringComparer.OrdinalIgnoreCase);

    public static void TaskStarted(string taskId) => RunningSince[taskId] = DateTime.UtcNow;

    public static void TaskFinished(string taskId) => RunningSince.TryRemove(taskId, out _);

    /// <summary>When a running task started. One that started before Medic was watching counts from when it was first seen.</summary>
    internal static DateTime RunningSinceUtc(string taskId) => RunningSince.GetOrAdd(taskId, _ => DateTime.UtcNow);

    /// <summary>
    /// Roughly how long a running task has left: from its progress so far, leaning on how long it
    /// usually takes while the progress is still too small to go on. Null when there's nothing to go on.
    /// </summary>
    public static TimeSpan? Remaining(double? percent, TimeSpan elapsed, double? typicalMinutes)
    {
        double? byProgress = percent is double p && p >= 1 && elapsed.TotalSeconds >= 20
            ? elapsed.TotalSeconds * (100 - p) / p
            : null;
        double? byHistory = typicalMinutes is double t && t > 0 ? Math.Max(0, (t * 60) - elapsed.TotalSeconds) : null;

        double? seconds = (byProgress, byHistory) switch
        {
            (double a, double b) when percent < 20 => (a * percent!.Value / 20) + (b * (1 - (percent!.Value / 20))),
            (double a, _) => a,
            (null, double b) when b > 0 => b,
            _ => null
        };

        return seconds is double s ? TimeSpan.FromSeconds(s) : null;
    }

    public static string FormatRemaining(TimeSpan left) =>
        left.TotalMinutes < 1 ? "under a minute left"
        : left.TotalMinutes < 90 ? $"about {Math.Ceiling(left.TotalMinutes):0} min left"
        : $"about {left.TotalHours:0.#} hours left";

    // Two meters: the Dashboard's "now" figure and the 5-minute line each measure since their own previous
    // reading, so polling the Dashboard doesn't shrink the line's 5 minutes to the time since the last poll.
    private static readonly CpuMeter NowMeter = new();
    private static readonly CpuMeter LineMeter = new();

    /// <summary>Jellyfin's CPU use since the Dashboard's previous reading, as a share of all CPU threads.</summary>
    public static double CpuPercent() => NowMeter.Read();

    /// <summary>Called every 5 minutes by the usage sampler to build the Dashboard's CPU line.</summary>
    public static void Record()
    {
        double value = LineMeter.Read();
        lock (Sync)
        {
            History.Enqueue(value);
            while (History.Count > 24)
            {
                History.Dequeue();
            }
        }
    }

    /// <summary>Jellyfin's own CPU use between one reading and the next.</summary>
    private sealed class CpuMeter
    {
        private readonly object _sync = new();
        private (DateTime Wall, TimeSpan Cpu)? _last;
        private double _value;

        public double Read()
        {
            (DateTime Wall, TimeSpan Cpu) start;
            lock (_sync)
            {
                var now = Sample();
                if (_last is { } prev)
                {
                    // Two Dashboards polling within a second share one reading rather than measuring a sliver of time.
                    if ((now.Wall - prev.Wall).TotalSeconds >= 1)
                    {
                        _value = Percent(prev, now);
                        _last = now;
                    }

                    return _value;
                }

                start = now;
            }

            // First reading: measure over a short moment, outside the lock so no other request waits for it.
            Thread.Sleep(500);
            var end = Sample();
            lock (_sync)
            {
                _value = Percent(start, end);
                _last = end;
                return _value;
            }
        }

        private static (DateTime Wall, TimeSpan Cpu) Sample()
        {
            using var process = Process.GetCurrentProcess();
            return (DateTime.UtcNow, process.TotalProcessorTime);
        }

        private static double Percent((DateTime Wall, TimeSpan Cpu) from, (DateTime Wall, TimeSpan Cpu) to)
        {
            double wall = (to.Wall - from.Wall).TotalMilliseconds;
            double cpu = (to.Cpu - from.Cpu).TotalMilliseconds;
            return wall <= 0 ? 0 : Math.Round(Math.Clamp(cpu / wall / Environment.ProcessorCount * 100, 0, 100), 1);
        }
    }

    public static NowSnapshot Snapshot(ISessionManager sessions, ITaskManager tasks, IApplicationPaths? paths = null)
    {
        var snap = new NowSnapshot { CpuPercent = CpuPercent(), Resources = ResourceMonitor.Latest };
        lock (Sync)
        {
            snap.CpuHistory = History.ToList();
        }

        var process = Process.GetCurrentProcess();
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        snap.MemoryGb = Math.Round(process.WorkingSet64 / 1073741824d, 1);
        snap.MemoryLimitGb = Math.Round(available / 1073741824d, 1);
        snap.MemoryPercent = available > 0 ? Math.Round(Math.Clamp(process.WorkingSet64 * 100d / available, 0, 100), 1) : 0;

        try
        {
            var active = sessions.Sessions.Cast<object>()
                .Where(s => SettingsReader.Get(s, "LastActivityDate") is DateTime d && d > DateTime.UtcNow.AddMinutes(-10))
                .ToList();
            snap.ActiveDevices = active.Count;
            var playing = sessions.Sessions.Cast<object>().Where(s => SettingsReader.Get(s, "NowPlayingItem") is not null).ToList();
            snap.Watching = playing.Count;
            snap.Transcoding = playing.Count(s => string.Equals(SettingsReader.Text(s, "PlayState.PlayMethod"), "Transcode", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Sessions unavailable: leave the counts at zero.
        }

        var running = tasks.ScheduledTasks.Where(t => t.State == TaskState.Running).ToList();
        var history = running.Count > 0 && paths is not null ? ScheduleStorage.LoadProfile(paths) : new Dictionary<string, ReliabilityRecord>();
        snap.RunningTasks = running.Select(t =>
            {
                string id = t.Id.ToString();
                var since = RunningSinceUtc(id);
                history.TryGetValue(id, out var rec);
                double? typical = rec is { TotalRuns: > 0 } || t.LastExecutionResult is not null ? ScheduleStorage.TypicalMinutes(t, rec) : null;
                var left = Remaining(t.CurrentProgress, DateTime.UtcNow - since, typical);
                string progress = t.CurrentProgress is double p ? $"{p:0}%" : string.Empty;
                string eta = left is { } l ? FormatRemaining(l) : string.Empty;
                string detail = string.Join(", ", new[] { progress, eta }.Where(x => x.Length > 0));
                return detail.Length > 0 ? $"{t.Name} ({detail})" : t.Name;
            })
            .ToList();

        return snap;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
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

    public int Watching { get; set; }

    public int Transcoding { get; set; }

    public int ActiveDevices { get; set; }

    public List<string> RunningTasks { get; set; } = new();
}

/// <summary>What Jellyfin is doing right now, for the Dashboard. Cheap: no database queries.</summary>
public static class ServerNow
{
    private static readonly object Sync = new();
    private static readonly Queue<double> History = new();
    private static (DateTime Wall, TimeSpan Cpu)? _last;
    private static readonly ConcurrentDictionary<string, DateTime> RunningSince = new(StringComparer.OrdinalIgnoreCase);

    public static void TaskStarted(string taskId) => RunningSince[taskId] = DateTime.UtcNow;

    public static void TaskFinished(string taskId) => RunningSince.TryRemove(taskId, out _);

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

    /// <summary>Jellyfin's CPU use since the previous reading, as a share of all CPU threads.</summary>
    public static double CpuPercent()
    {
        var process = Process.GetCurrentProcess();
        lock (Sync)
        {
            var now = (DateTime.UtcNow, process.TotalProcessorTime);
            if (_last is not { } prev || (now.UtcNow - prev.Wall).TotalSeconds < 1)
            {
                // No recent reading: measure over a short moment.
                var start = (DateTime.UtcNow, process.TotalProcessorTime);
                Thread.Sleep(500);
                process.Refresh();
                now = (DateTime.UtcNow, process.TotalProcessorTime);
                prev = (start.UtcNow, start.TotalProcessorTime);
            }

            double wall = (now.UtcNow - prev.Wall).TotalMilliseconds;
            double cpu = (now.TotalProcessorTime - prev.Cpu).TotalMilliseconds;
            _last = (now.UtcNow, now.TotalProcessorTime);
            return wall <= 0 ? 0 : Math.Round(Math.Clamp(cpu / wall / Environment.ProcessorCount * 100, 0, 100), 1);
        }
    }

    /// <summary>Called every 5 minutes by the usage sampler to build the Dashboard's CPU line.</summary>
    public static void Record()
    {
        double value = CpuPercent();
        lock (Sync)
        {
            History.Enqueue(value);
            while (History.Count > 24)
            {
                History.Dequeue();
            }
        }
    }

    public static NowSnapshot Snapshot(ISessionManager sessions, ITaskManager tasks, IApplicationPaths? paths = null)
    {
        var snap = new NowSnapshot { CpuPercent = CpuPercent() };
        lock (Sync)
        {
            snap.CpuHistory = History.ToList();
        }

        var process = Process.GetCurrentProcess();
        snap.MemoryGb = Math.Round(process.WorkingSet64 / 1073741824d, 1);
        snap.MemoryLimitGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824d, 1);

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
                var since = RunningSince.GetOrAdd(id, _ => DateTime.UtcNow); // started before Medic was watching: count from now
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;

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

    public static NowSnapshot Snapshot(ISessionManager sessions, ITaskManager tasks)
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

        snap.RunningTasks = tasks.ScheduledTasks
            .Where(t => t.State == TaskState.Running)
            .Select(t => t.CurrentProgress is double p ? $"{t.Name} ({p:0}%)" : t.Name)
            .ToList();

        return snap;
    }
}

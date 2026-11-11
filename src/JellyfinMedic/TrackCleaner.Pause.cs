using System.Globalization;

namespace JellyfinMedic.Services;

/// <summary>Track cleanup: stopping, pausing, the time window, and holding off while people watch.</summary>
public static partial class TrackCleaner
{
    public static void Stop()
    {
        try { _cancel?.Cancel(); } catch { /* already done */ }
    }

    // ---------- Pause, time window, and holding off while people watch ----------

    private static volatile bool _paused;

    /// <summary>How many people are playing something right now. Set by the controller when a run starts.</summary>
    public static Func<int>? WatchingCount { get; set; }

    /// <summary>Pauses after the files already in progress finish. Resume carries on where it left off.</summary>
    public static void Pause()
    {
        _paused = true;
        lock (Sync) { Progress.Paused = Progress.Running; }
    }

    public static void Resume()
    {
        _paused = false;
        lock (Sync) { Progress.Paused = false; }
    }

    private static async Task WaitUntilAllowedAsync(PluginConfiguration startCfg, CancellationToken ct)
    {
        bool waited = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Read settings live, so changing the window or the watching option applies mid-run.
            var cfg = Plugin.Instance?.Configuration ?? startCfg;
            string? reason = WaitReason(cfg);
            if (reason is null)
            {
                break;
            }

            lock (Sync)
            {
                Progress.Waiting = reason;
                Progress.WaitingSinceUtc ??= DateTime.UtcNow;
            }

            waited = true;
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }

        if (waited)
        {
            lock (Sync)
            {
                if (Progress.WaitingSinceUtc is { } since)
                {
                    Progress.WaitedSeconds += (DateTime.UtcNow - since).TotalSeconds;
                }

                Progress.WaitingSinceUtc = null;
                Progress.Waiting = null;
            }
        }
    }

    private static string? WaitReason(PluginConfiguration cfg)
    {
        if (_paused)
        {
            return "Paused";
        }

        if (cfg.TracksWindowEnabled && !InWindow(DateTime.Now.Hour, cfg.TracksWindowStartHour, cfg.TracksWindowEndHour))
        {
            return string.Format(CultureInfo.InvariantCulture, "Waiting for {0:00}:00–{1:00}:00", cfg.TracksWindowStartHour, cfg.TracksWindowEndHour);
        }

        if (cfg.TracksPauseWhileWatching)
        {
            int watching = 0;
            try { watching = WatchingCount?.Invoke() ?? 0; } catch { /* treat as nobody */ }
            if (watching > 0)
            {
                return watching == 1 ? "Waiting: someone is watching" : $"Waiting: {watching} people are watching";
            }
        }

        return null;
    }

    private static bool InWindow(int hour, int start, int end)
    {
        start = Math.Clamp(start, 0, 23);
        end = Math.Clamp(end, 0, 23);
        if (start == end)
        {
            return true;
        }

        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }
}

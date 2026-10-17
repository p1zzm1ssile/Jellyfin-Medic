using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Watches memory and running tasks. If memory climbs past a ceiling while more than one heavy
/// task is running, it stops the extra heavy tasks (keeping the one that started first), waits for
/// memory to recover, then restarts the stopped ones one at a time so they don't pile up again.
///
/// It also watches the whole server's memory, other programs included (Linux): a heavy task that starts while the
/// server is past the ceiling waits until memory is back, or two hours at most. Work already under way is left alone.
///
/// It only ever stops HEAVY tasks (scans, image/segment extraction, big syncs) — never light
/// upkeep — and it records every action for the Dashboard. It can't pause a running task without
/// stopping it, so it protects the server by stopping-and-restarting rather than freezing.
/// </summary>
public sealed class LoadGuard : IHostedService, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(20);

    // A heavy task that starts while the whole server is short of memory waits this long at most.
    internal static readonly TimeSpan MaxWait = TimeSpan.FromHours(2);

    // A stopped task is started again only if it was stopped this recently, before or after a restart; an older one
    // will have had its next scheduled run by then.
    internal static readonly TimeSpan KeepAcrossRestart = TimeSpan.FromHours(24);

    // A task counts as just started for three checks.
    private static readonly TimeSpan JustStarted = TimeSpan.FromSeconds(60);
    // Jellyfin's own heavy tasks by key (their names are translated into the server's language),
    // and tasks from plugins by words in their names.
    private static readonly HashSet<string> HeavyKeys = new(StringComparer.Ordinal)
    {
        "RefreshLibrary", "RefreshGuide", "RefreshInternetChannels", "RefreshChapterImages", "RefreshTrickplayImages",
        "MoveTrickplayImages", "TaskExtractMediaSegments", "KeyframeExtraction", "AudioNormalization", "DownloadSubtitles", "RefreshPeople"
    };

    private static readonly string[] HeavyWords =
    {
        "Scan Media Library", "Refresh Guide", "Refresh Channels", "Chapter", "Trickplay",
        "Segment", "Keyframe", "Intro", "Audio Normali", "Subtitle", "Metadata", "People",
        "Xtream", "Scan", "Refresh"
    };

    private readonly ITaskManager _tasks;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<LoadGuard> _logger;
    private Timer? _timer;
    private int _busy;

    // Tasks we stopped and intend to restart once there's headroom, oldest first. Saved to disk on every change, so a
    // restart of Jellyfin (Medic's own restart tasks included) doesn't forget them.
    private readonly List<PausedTask> _deferred = new();
    private DateTime _lastActionUtc = DateTime.MinValue;

    // Tasks the guard started again, which the server check lets through when they start (task id, until when).
    private readonly Dictionary<string, DateTime> _letThroughUntil = new();

    // The same list for the Dashboard, which can't reach this service. Replaced on every change and never
    // changed afterwards, so readers need no lock.
    private static PausedTask[] _paused = Array.Empty<PausedTask>();

    // When each running task started, from Jellyfin's TaskExecuting event, so "the one that started first"
    // really is. A running task's LastExecutionResult is its previous run, so it can't be used for this.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _startedUtc = new();

    public LoadGuard(ITaskManager tasks, IApplicationPaths paths, ILogger<LoadGuard> logger)
    {
        _tasks = tasks;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Tasks LoadGuard stopped and will restart once memory recovers, oldest first.</summary>
    internal static IReadOnlyList<PausedTask> Paused => Volatile.Read(ref _paused);

    private string DeferredPath => Path.Combine(ScheduleStorage.DataDir(_paths), "loadguard_deferred.json");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<PausedTask> saved;
            lock (ScheduleStorage.SyncRoot)
            {
                saved = ScheduleStorage.ReadList<PausedTask>(DeferredPath);
            }

            var now = DateTime.UtcNow;
            _deferred.AddRange(Restore(saved, now));
            foreach (var task in saved.Where(p => p.TaskId.Length > 0 && Expired(p, now) && !_deferred.Any(d => d.TaskId == p.TaskId)).DistinctBy(p => p.TaskId))
            {
                LogExpired(task);
            }

            Volatile.Write(ref _paused, _deferred.ToArray());
            if (_deferred.Count != saved.Count)
            {
                SaveDeferred();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin Medic: couldn't read the tasks LoadGuard was holding back before the restart.");
        }

        _tasks.TaskExecuting += OnTaskExecuting;
        _timer = new Timer(_ => Tick(), null, CheckEvery, CheckEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tasks.TaskExecuting -= OnTaskExecuting;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private void OnTaskExecuting(object? sender, Jellyfin.Data.Events.GenericEventArgs<IScheduledTaskWorker> e)
    {
        if (e?.Argument is { } worker)
        {
            _startedUtc[worker.Id.ToString()] = DateTime.UtcNow;
        }
    }

    public void Dispose() => _timer?.Dispose();

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var settings = Plugin.Instance?.Configuration;
            if (settings is null)
            {
                return;
            }

            DropStarted();
            DropExpired(DateTime.UtcNow);
            if (!settings.LoadGuardEnabled)
            {
                ReleaseAll();
                return;
            }

            double usedPercent = MemoryUsedPercent(out double usedGb, out double limitGb);
            double? serverPercent = ServerMemory.UsedPercent(out double serverUsedGb, out double serverTotalGb);
            int highMark = Math.Clamp(settings.MemoryCeilingPercent, 60, 95);
            int clearMark = highMark - 12; // recover to comfortably below the ceiling before restarting

            var heavyRunning = _tasks.ScheduledTasks
                .Where(t => t.State == TaskState.Running && IsHeavy(t))
                .OrderBy(t => StartedAt(t))
                .ToList();

            // High memory + more than one heavy task: stop the extras (keep the earliest).
            if (usedPercent >= highMark && heavyRunning.Count > 1)
            {
                foreach (var worker in heavyRunning.Skip(1))
                {
                    try
                    {
                        _tasks.Cancel(worker);
                        Defer(worker);

                        Record("stopped", worker.Name, usedGb, limitGb);
                        _logger.LogWarning("Jellyfin Medic: memory at {Used:0}% of {Limit:0} GB with {Count} heavy tasks running; stopped '{Task}' to protect the server.", usedPercent, limitGb, heavyRunning.Count, worker.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Jellyfin Medic: couldn't stop '{Task}' under memory pressure.", worker.Name);
                    }
                }

                _lastActionUtc = DateTime.UtcNow;
                return;
            }

            // The whole server is short of memory, other programs included: a heavy task that has just started waits.
            var now = DateTime.UtcNow;
            foreach (var id in _letThroughUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            {
                _letThroughUntil.Remove(id);
            }

            var waiting = heavyRunning.Where(w => HoldBack(serverPercent, highMark, now - StartedAt(w), _letThroughUntil.ContainsKey(w.Id.ToString()))).ToList();
            if (waiting.Count > 0)
            {
                foreach (var worker in waiting)
                {
                    try
                    {
                        _tasks.Cancel(worker);
                        Defer(worker);
                        Record("postponed", worker.Name, serverUsedGb, serverTotalGb);
                        _logger.LogWarning("Jellyfin Medic: the server's memory is at {Used:0}% of {Total:0} GB; postponed '{Task}' until it is below {Mark}%, or two hours at most.", serverPercent, serverTotalGb, worker.Name, clearMark);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Jellyfin Medic: couldn't postpone '{Task}' while the server is short of memory.", worker.Name);
                    }
                }

                _lastActionUtc = now;
                return;
            }

            // Memory has recovered and nothing heavy is running: restart one deferred task.
            bool settled = now - _lastActionUtc > TimeSpan.FromMinutes(2);
            if (_deferred.Count > 0 && settled && CanRestart(usedPercent, serverPercent, highMark, clearMark, heavyRunning.Count, now - _deferred[0].StoppedUtc))
            {
                string id = _deferred[0].TaskId;
                var worker = _tasks.ScheduledTasks.FirstOrDefault(t => t.Id.ToString() == id);
                if (worker is null)
                {
                    DropFirst(); // the task is gone (its plugin was removed)
                }
                else if (worker.State == TaskState.Idle)
                {
                    // Only off the list once it has really been started again; while it's still cancelling, it waits its turn.
                    try
                    {
                        _tasks.Execute(worker, new TaskOptions());
                        _letThroughUntil[id] = now + TimeSpan.FromMinutes(10);
                        DropFirst();
                        Record("restarted", worker.Name, usedGb, limitGb);
                        _logger.LogInformation("Jellyfin Medic: restarted deferred task '{Task}' with memory at {Used:0}% for Jellyfin and {Server} for the whole server.", worker.Name, usedPercent, serverPercent is { } server ? server.ToString("0", CultureInfo.InvariantCulture) + "%" : "an unknown share");
                        _lastActionUtc = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        DropFirst(); // starting it fails, so trying again every round wouldn't help
                        _logger.LogError(ex, "Jellyfin Medic: couldn't restart deferred task '{Task}'.", worker.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jellyfin Medic: load guard error.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Whether a running heavy task should wait: it started under a minute ago, the whole server's memory (other
    /// programs included) is past the ceiling, and it isn't a task the guard has just started again.
    /// </summary>
    internal static bool HoldBack(double? serverPercent, int highMark, TimeSpan runningFor, bool letThrough) =>
        serverPercent is { } server && server >= highMark && runningFor < JustStarted && !letThrough;

    /// <summary>
    /// Whether the oldest waiting task can start again: nothing heavy runs, Jellyfin's own memory is below the restart
    /// mark, and so is the whole server's. Once the task has waited MaxWait, the server's memory no longer counts and
    /// Jellyfin's only has to be below the ceiling, so a Jellyfin that settles between the two marks can't hold it for ever.
    /// </summary>
    internal static bool CanRestart(double jellyfinPercent, double? serverPercent, int highMark, int clearMark, int heavyRunning, TimeSpan waited) =>
        heavyRunning == 0 && (waited >= MaxWait
            ? jellyfinPercent < highMark
            : jellyfinPercent <= clearMark && (serverPercent is not { } server || server <= clearMark));

    private static bool IsHeavy(IScheduledTaskWorker task) =>
        HeavyKeys.Contains(task.ScheduledTask.Key) || HeavyWords.Any(w => task.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private DateTime StartedAt(IScheduledTaskWorker worker)
    {
        // A task already running when Medic started has been going longest, so it counts as first.
        return _startedUtc.TryGetValue(worker.Id.ToString(), out var started) ? started : DateTime.MinValue;
    }

    /// <summary>Puts a task it just stopped on the restart list, once.</summary>
    internal void Defer(IScheduledTaskWorker worker)
    {
        string id = worker.Id.ToString();
        if (!_deferred.Any(d => d.TaskId == id))
        {
            _deferred.Add(new PausedTask { TaskId = id, TaskName = worker.Name, StoppedUtc = DateTime.UtcNow });
            Volatile.Write(ref _paused, _deferred.ToArray());
            SaveDeferred();
        }
    }

    /// <summary>
    /// Takes off the list each task that has started since it was stopped, on its schedule or by hand. It has had its
    /// run, so starting it again later would run it twice.
    /// </summary>
    internal void DropStarted()
    {
        var started = _deferred.Where(d => _startedUtc.TryGetValue(d.TaskId, out var at) && at > d.StoppedUtc).ToList();
        if (started.Count == 0)
        {
            return;
        }

        foreach (var task in started)
        {
            _deferred.Remove(task);
            _logger.LogInformation("Jellyfin Medic: '{Task}' started again on its own, so LoadGuard no longer holds it back.", task.TaskName);
        }

        Volatile.Write(ref _paused, _deferred.ToArray());
        SaveDeferred();
    }

    /// <summary>Takes off the list each task stopped a day ago or more, rather than start it again on top of its next scheduled run.</summary>
    internal void DropExpired(DateTime nowUtc)
    {
        var expired = _deferred.Where(d => Expired(d, nowUtc)).ToList();
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var task in expired)
        {
            _deferred.Remove(task);
            LogExpired(task);
        }

        Volatile.Write(ref _paused, _deferred.ToArray());
        SaveDeferred();
    }

    private static bool Expired(PausedTask task, DateTime nowUtc) => nowUtc - task.StoppedUtc >= KeepAcrossRestart;

    private void LogExpired(PausedTask task) =>
        _logger.LogInformation("Jellyfin Medic: LoadGuard held back '{Task}' for over 24 hours, so it lets it go to run at its next scheduled time.", task.TaskName);

    /// <summary>
    /// Lets go of every held task when LoadGuard is switched off, to run at its next scheduled time, rather than start
    /// stale tasks if it's switched on days later. Each one is recorded, so the Dashboard says where it went.
    /// </summary>
    internal void ReleaseAll()
    {
        if (_deferred.Count == 0)
        {
            return;
        }

        var released = _deferred.ToList();
        _logger.LogInformation("Jellyfin Medic: LoadGuard is off, so the {Count} task(s) it stopped will run at their next scheduled time.", released.Count);
        _deferred.Clear();
        Volatile.Write(ref _paused, Array.Empty<PausedTask>());
        SaveDeferred();

        MemoryUsedPercent(out double usedGb, out double limitGb);
        foreach (var task in released)
        {
            Record("released", task.TaskName, usedGb, limitGb);
        }
    }

    /// <summary>The held tasks' names for a message, as "A", "B" and "C", or null when none is held.</summary>
    internal static string? HeldNames(IReadOnlyList<PausedTask> held)
    {
        var names = held.Select(p => "\"" + p.TaskName + "\"").ToList();
        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
        };
    }

    /// <summary>Takes the oldest task off the restart list.</summary>
    internal void DropFirst()
    {
        _deferred.RemoveAt(0);
        Volatile.Write(ref _paused, _deferred.ToArray());
        SaveDeferred();
    }

    /// <summary>The saved list as it's taken back after a restart: each task once, and none stopped over a day ago.</summary>
    internal static List<PausedTask> Restore(IEnumerable<PausedTask> saved, DateTime nowUtc) =>
        saved.Where(p => p.TaskId.Length > 0 && !Expired(p, nowUtc))
            .GroupBy(p => p.TaskId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(p => p.StoppedUtc)
            .ToList();

    private void SaveDeferred()
    {
        try
        {
            lock (ScheduleStorage.SyncRoot)
            {
                ScheduleStorage.WriteJson(DeferredPath, _deferred);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin Medic: couldn't save the tasks LoadGuard is holding back; a restart now would forget them.");
        }
    }

    /// <summary>
    /// Memory in use as a percentage of what the container is allowed. Prefers the cgroup limit
    /// (what Docker actually caps Jellyfin at) and falls back to the .NET available-memory figure.
    /// </summary>
    private static double MemoryUsedPercent(out double usedGb, out double limitGb)
    {
        const double GiB = 1024d * 1024 * 1024;
        double limitBytes = CgroupLimit() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        double usedBytes = CgroupUsage() ?? Process.GetCurrentProcess().WorkingSet64;
        limitGb = Math.Round(limitBytes / GiB, 1);
        usedGb = Math.Round(usedBytes / GiB, 1);
        return limitBytes <= 0 ? 0 : Math.Clamp(usedBytes / limitBytes * 100, 0, 100);
    }

    /// <summary>
    /// The memory cgroup folders to read, Jellyfin's own first. In a container that's the container's (the root
    /// as seen from inside); on a host it's the service's, as the root there is the whole machine.
    /// </summary>
    private static readonly Lazy<string[]> CgroupDirs = new(FindCgroupDirs);

    private static string[] FindCgroupDirs()
    {
        var dirs = new List<string>();
        try
        {
            // cgroup v2: "0::/path"; v1: "N:memory,...:/path".
            foreach (var line in File.ReadLines("/proc/self/cgroup"))
            {
                var parts = line.Split(':', 3);
                if (parts.Length < 3)
                {
                    continue;
                }

                string path = parts[2].Trim().TrimEnd('/');
                if (parts[0] == "0" && parts[1].Length == 0)
                {
                    dirs.Add("/sys/fs/cgroup" + path);
                }
                else if (parts[1].Split(',').Contains("memory"))
                {
                    dirs.Add("/sys/fs/cgroup/memory" + path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not Linux, or no /proc: only the defaults below.
        }

        dirs.Add("/sys/fs/cgroup");
        dirs.Add("/sys/fs/cgroup/memory");
        return dirs.Distinct().Where(Directory.Exists).ToArray();
    }

    /// <summary>The memory limit on Jellyfin's cgroup in bytes, or null when there's none.</summary>
    internal static double? CgroupLimit()
    {
        double? v = CgroupDirs.Value
            .Select(d => ReadNumber(Path.Combine(d, "memory.max")) ?? ReadNumber(Path.Combine(d, "memory.limit_in_bytes")))
            .FirstOrDefault(found => found is not null);
        // A "no limit" cgroup reports a huge sentinel value; treat that as unknown.
        return v is > 0 and < 1e15 ? v : null;
    }

    private static double? CgroupUsage()
    {
        string? dir = CgroupDirs.Value.FirstOrDefault(d => File.Exists(Path.Combine(d, "memory.current")) || File.Exists(Path.Combine(d, "memory.usage_in_bytes")));
        if (dir is null)
        {
            return null;
        }

        double? current = ReadNumber(Path.Combine(dir, "memory.current")) ?? ReadNumber(Path.Combine(dir, "memory.usage_in_bytes"));
        if (current is null)
        {
            return null;
        }

        // Subtract reclaimable file cache so we measure real pressure, not cache that frees on demand.
        double inactive = ReadStatValue(Path.Combine(dir, "memory.stat"), "inactive_file")
                          ?? ReadStatValue(Path.Combine(dir, "memory.stat"), "total_inactive_file") ?? 0;
        return Math.Max(0, current.Value - inactive);
    }

    private static double? ReadNumber(string path)
    {
        try
        {
            return File.Exists(path) && double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadStatValue(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(key + " ", StringComparison.Ordinal) &&
                    double.TryParse(line[(key.Length + 1)..].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                {
                    return v;
                }
            }
        }
        catch
        {
            // Ignore.
        }

        return null;
    }

    private void Record(string action, string task, double usedGb, double limitGb) =>
        LoadGuardLog.Add(_paths, new LoadGuardEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Action = action,
            TaskName = task,
            MemoryUsedGb = usedGb,
            MemoryLimitGb = limitGb
        });
}

public class LoadGuardEvent
{
    public DateTime TimestampUtc { get; set; }

    public string Action { get; set; } = string.Empty; // stopped | postponed | restarted | released

    public string TaskName { get; set; } = string.Empty;

    public double MemoryUsedGb { get; set; }

    public double MemoryLimitGb { get; set; }
}

public static class LoadGuardLog
{
    private static readonly object Sync = new();
    private const int Keep = 100;

    private static string Path(IApplicationPaths paths) =>
        System.IO.Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "load_guard.json");

    public static void Add(IApplicationPaths paths, LoadGuardEvent entry)
    {
        lock (Sync)
        {
            var list = Load(paths);
            list.Insert(0, entry);
            if (list.Count > Keep)
            {
                list = list.Take(Keep).ToList();
            }

            try
            {
                string path = Path(paths);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                ScheduleStorage.WriteText(path, JsonSerializer.Serialize(list));
            }
            catch
            {
                // Logging is best-effort.
            }
        }
    }

    public static List<LoadGuardEvent> Load(IApplicationPaths paths)
    {
        try
        {
            string path = Path(paths);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<LoadGuardEvent>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }
}

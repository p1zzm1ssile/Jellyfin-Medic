using Jellyfin.Data.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace JellyfinMedic.Tests;

/// <summary>Jellyfin's folders, all under one temporary folder that the test deletes.</summary>
internal sealed class FakeApplicationPaths : IApplicationPaths
{
    public string Root { get; } = Directory.CreateTempSubdirectory("medic-tests-").FullName;

    public string ProgramDataPath => Root;

    public string WebPath => Path.Combine(Root, "web");

    public string ProgramSystemPath => Path.Combine(Root, "system");

    public string DataPath => Path.Combine(Root, "data");

    public string ImageCachePath => Path.Combine(Root, "cache", "images");

    public string PluginsPath => Path.Combine(Root, "plugins");

    public string PluginConfigurationsPath => Path.Combine(Root, "plugins", "configurations");

    public string LogDirectoryPath => Path.Combine(Root, "log");

    public string ConfigurationDirectoryPath => Path.Combine(Root, "config");

    public string SystemConfigurationFilePath => Path.Combine(Root, "config", "system.xml");

    public string CachePath => Path.Combine(Root, "cache");

    public string TempDirectory => Path.Combine(Root, "temp");

    public string VirtualDataPath => Path.Combine(Root, "data");

    public string TrickplayPath => Path.Combine(Root, "data", "trickplay");

    public string BackupPath => Path.Combine(Root, "backups");

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
    }
}

/// <summary>A task manager whose only job is to tell listeners that a task finished.</summary>
internal sealed class FakeTaskManager : ITaskManager
{
    public event EventHandler<GenericEventArgs<IScheduledTaskWorker>>? TaskExecuting;

    public event EventHandler<TaskCompletionEventArgs>? TaskCompleted;

    public IReadOnlyList<IScheduledTaskWorker> ScheduledTasks { get; set; } = Array.Empty<IScheduledTaskWorker>();

    public void Complete(IScheduledTaskWorker worker, TaskCompletionStatus status)
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        TaskExecuting?.Invoke(this, new GenericEventArgs<IScheduledTaskWorker>(worker));
        TaskCompleted?.Invoke(this, new TaskCompletionEventArgs(worker, new TaskResult
        {
            Name = worker.Name,
            Key = worker.ScheduledTask.Key,
            Id = worker.Id,
            Status = status,
            StartTimeUtc = start,
            EndTimeUtc = start.AddMinutes(5)
        }));
    }

    public void AddTasks(IEnumerable<IScheduledTask> tasks) => throw new NotSupportedException();

    public void Cancel(IScheduledTaskWorker task) => throw new NotSupportedException();

    public void CancelIfRunning<T>() where T : IScheduledTask => throw new NotSupportedException();

    public void CancelIfRunningAndQueue<T>() where T : IScheduledTask => throw new NotSupportedException();

    public void CancelIfRunningAndQueue<T>(TaskOptions options) where T : IScheduledTask => throw new NotSupportedException();

    public Task Execute(IScheduledTaskWorker task, TaskOptions options) => throw new NotSupportedException();

    public void Execute<T>() where T : IScheduledTask => throw new NotSupportedException();

    public void QueueIfNotRunning<T>() where T : IScheduledTask => throw new NotSupportedException();

    public void QueueScheduledTask<T>() where T : IScheduledTask => throw new NotSupportedException();

    public void QueueScheduledTask<T>(TaskOptions options) where T : IScheduledTask => throw new NotSupportedException();

    public void QueueScheduledTask(IScheduledTask task, TaskOptions options) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}

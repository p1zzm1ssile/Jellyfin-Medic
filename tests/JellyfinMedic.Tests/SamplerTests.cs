using System.Reflection;
using JellyfinMedic.Services;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace JellyfinMedic.Tests;

public sealed class SamplerTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    // StorageWatch.RecordKnown remembers the day it last recorded for the whole process, and even a sampler with no
    // drives to read succeeds, so every test here blocks it: after one success the rest of the day records nothing.
    public SamplerTests() => DamageDiskHistoryAndBlockSettingItAside();

    [Fact]
    public void A_disk_reading_that_failed_is_tried_again_the_same_day()
    {

        Assert.ThrowsAny<IOException>(() => StorageWatch.RecordKnown(_paths.PluginConfigurationsPath));
        Assert.ThrowsAny<IOException>(() => StorageWatch.RecordKnown(_paths.PluginConfigurationsPath));
    }

    [Fact]
    public void Disk_space_isnt_said_to_work_again_while_nothing_is_recorded()
    {
        var log = new ListLogger();
        var sampler = new UsageSampler(Sessions.Watching(), _paths, log);

        sampler.Sample();
        sampler.Sample();

        Assert.Single(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("disk space", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Level == LogLevel.Information && l.Text.Contains("disk space", StringComparison.Ordinal));
    }

    [Fact]
    public void When_sessions_cant_be_read_the_recorders_that_dont_need_them_still_run()
    {
        var log = new ListLogger();
        var sampler = new UsageSampler(Sessions.Failing(), _paths, log);

        sampler.Sample();

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("who is watching", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("disk space", StringComparison.Ordinal));
        // With no count of who is watching, an hour would be recorded as empty.
        Assert.False(File.Exists(UsageStore.FilePath(_paths)));
    }

    [Fact]
    public void Who_is_watching_says_when_it_works_again_and_warns_again_on_the_next_failure()
    {
        var sessions = Sessions.Failing();
        var log = new ListLogger();
        var sampler = new UsageSampler(sessions, _paths, log);

        sampler.Sample();
        Sessions.Fail(sessions, false);
        sampler.Sample();
        Sessions.Fail(sessions, true);
        sampler.Sample();

        Assert.Single(log.Lines, l => l.Level == LogLevel.Information && l.Text.Contains("who is watching works again", StringComparison.Ordinal));
        Assert.Equal(2, log.Lines.Count(l => l.Level == LogLevel.Warning && l.Text.Contains("who is watching", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_damaged_usage_profile_that_cant_be_set_aside_reads_as_empty()
    {
        string path = UsageStore.FilePath(_paths);
        Damage(path);

        var profile = UsageStore.Load(_paths);

        Assert.Equal(0, profile.TotalSamples);
        Assert.Equal(168, profile.Buckets.Count);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_usage_profile_that_cant_be_set_aside_isnt_written_over()
    {
        string path = UsageStore.FilePath(_paths);
        Damage(path);

        Assert.ThrowsAny<IOException>(() => UsageStore.Record(_paths, DateTime.Now, 1, 0));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_load_profile_that_cant_be_set_aside_reads_as_empty()
    {
        string path = HostLoadStore.FilePath(_paths);
        Damage(path);

        var profile = HostLoadStore.Load(_paths);

        Assert.Equal(0, profile.TotalSamples);
        Assert.Equal(168, profile.Buckets.Count);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_load_profile_that_cant_be_set_aside_isnt_written_over()
    {
        string path = HostLoadStore.FilePath(_paths);
        Damage(path);

        Assert.ThrowsAny<IOException>(() => HostLoadStore.Record(_paths, DateTime.Now, new HostLoadReading(10, 20, 5)));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private void DamageDiskHistoryAndBlockSettingItAside() =>
        Damage(Path.Combine(_paths.PluginConfigurationsPath, "JellyfinMedic", "disk_history.json"));

    // A folder already holding the .bad name stops the move on Linux, Windows and macOS alike, and unlike a
    // read-only folder it also stops it when the tests run as root, as they can in a container.
    private static void Damage(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Directory.CreateDirectory(path + ".bad");
        File.WriteAllText(Path.Combine(path + ".bad", "keep"), string.Empty);
    }

    /// <summary>A session manager that only lists sessions, which is all the sampler asks of it.</summary>
    private class Sessions : DispatchProxy
    {
        private bool _fail;

        public static ISessionManager Watching() => Create(fail: false);

        public static ISessionManager Failing() => Create(fail: true);

        public static void Fail(ISessionManager sessions, bool fail) => ((Sessions)(object)sessions)._fail = fail;

        private static ISessionManager Create(bool fail)
        {
            var sessions = Create<ISessionManager, Sessions>();
            ((Sessions)(object)sessions)._fail = fail;
            return sessions;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name != "get_" + nameof(ISessionManager.Sessions) ? throw new NotSupportedException(targetMethod?.Name)
            : _fail ? throw new InvalidOperationException("sessions are being rebuilt")
            : Array.Empty<SessionInfo>();
    }

    private sealed class ListLogger : ILogger<UsageSampler>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}

using System.Reflection;
using JellyfinMedic.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Tests;

public sealed class LibraryWalkCacheTests
{
    private readonly ManualClock _clock = new();
    private int _walks;

    [Fact]
    public void A_second_look_within_ten_minutes_does_not_walk_again()
    {
        var cache = new LibraryWalkCache<string>(_clock);

        cache.Get(Walk, CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1);
        string second = cache.Get(Walk, CancellationToken.None);

        Assert.Equal(1, _walks);
        Assert.Equal("walk 1", second);
    }

    [Fact]
    public void A_look_ten_minutes_later_walks_again()
    {
        var cache = new LibraryWalkCache<string>(_clock);

        cache.Get(Walk, CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(10);
        string second = cache.Get(Walk, CancellationToken.None);

        Assert.Equal(2, _walks);
        Assert.Equal("walk 2", second);
    }

    [Fact]
    public void A_look_after_clearing_walks_again()
    {
        var cache = new LibraryWalkCache<string>(_clock);

        cache.Get(Walk, CancellationToken.None);
        cache.Clear();
        cache.Get(Walk, CancellationToken.None);

        Assert.Equal(2, _walks);
    }

    [Fact]
    public void A_walk_that_found_nothing_usable_is_not_kept()
    {
        var cache = new LibraryWalkCache<string?>(_clock);

        cache.Get(_ => { _walks++; return null; }, CancellationToken.None);
        cache.Get(_ => { _walks++; return null; }, CancellationToken.None);

        Assert.Equal(2, _walks);
    }

    [Fact]
    public void A_walk_cleared_while_under_way_is_not_kept()
    {
        var cache = new LibraryWalkCache<string>(_clock);

        // The files change while the walk reads them, so what it found is already out of date.
        cache.Get(ct => { cache.Clear(); return Walk(ct); }, CancellationToken.None);
        cache.Get(Walk, CancellationToken.None);

        Assert.Equal(2, _walks);
    }

    [Fact]
    public void A_cancelled_look_does_not_walk()
    {
        var cache = new LibraryWalkCache<string>(_clock);

        Assert.Throws<OperationCanceledException>(() => cache.Get(Walk, new CancellationToken(canceled: true)));

        Assert.Equal(0, _walks);
    }

    private string Walk(CancellationToken ct) => "walk " + ++_walks;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>The walks behind the Cleanup and Tracks tabs, through the caches they share with the whole server.</summary>
[Collection(TrackRunCollection.Name)]
public sealed class LibraryWalkOwnersTests : IDisposable
{
    private static readonly LibraryFacts Films = new() { Id = Guid.NewGuid().ToString("N"), Name = "Films" };

    private readonly string _root = Directory.CreateTempSubdirectory("medic-tests-").FullName;
    private readonly CountingLibrary _library;

    public LibraryWalkOwnersTests()
    {
        Housekeeping.KnownStrm.Clear();
        TrackCleaner.KeptOriginalsWalk.Clear();
        string film = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Films")).FullName, "Film.mkv");
        File.WriteAllText(film, "a film");
        _library = CountingLibrary.Listing(() => new BaseItem[] { new Movie { Path = film }, new Movie { Path = Path.Combine(_root, "IPTV", "Channel.strm") } });
    }

    [Fact]
    public void Kept_originals_are_counted_once_within_ten_minutes()
    {
        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);
        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);

        Assert.Equal(1, _library.Listings);
    }

    [Fact]
    public void Kept_originals_are_counted_again_after_deleting_them()
    {
        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);
        TrackCleaner.DeleteOriginals(_library.Manager, new[] { Films });
        int before = _library.Listings;

        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);

        Assert.Equal(before + 1, _library.Listings);
    }

    [Fact]
    public async Task Kept_originals_are_counted_again_after_removing_duplicate_files()
    {
        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);
        await TrackCleaner.TidyEarlierCopies(_library.Manager, new[] { Films }, Path.Combine(_root, "ffprobe"));
        int before = _library.Listings;

        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);

        Assert.Equal(before + 1, _library.Listings);
    }

    [Fact]
    public async Task Kept_originals_are_counted_again_after_a_track_run()
    {
        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);

        // Without FFprobe the run stops before changing a file, but it still ends like any run.
        await TrackCleaner.RunAsync(_library.Manager, new[] { Films }, Path.Combine(_root, "ffmpeg"), new PluginConfiguration(), dryRun: false).WaitAsync(TimeSpan.FromSeconds(30));
        TrackCleaner.KeptOriginals(_library.Manager, new[] { Films }, CancellationToken.None);

        Assert.Equal(2, _library.Listings);
    }

    [Fact]
    public void A_cancelled_request_stops_counting_kept_originals()
    {
        using var cts = new CancellationTokenSource();
        var library = CountingLibrary.Listing(() => CancelOnFirst(cts, Path.Combine(_root, "Films")));

        Assert.ThrowsAny<OperationCanceledException>(() => TrackCleaner.KeptOriginals(library.Manager, new[] { Films }, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => TrackCleaner.KeptOriginals(library.Manager, new[] { Films }, cts.Token));
    }

    [Fact]
    public void Known_stream_files_are_read_once_within_ten_minutes()
    {
        var first = Housekeeping.KnownStrmPaths(_library.Manager, CancellationToken.None);
        var second = Housekeeping.KnownStrmPaths(_library.Manager, CancellationToken.None);

        Assert.Equal(1, _library.Listings);
        Assert.Contains(Path.Combine(_root, "IPTV", "Channel.strm"), second!);
        Assert.Same(first, second);
    }

    [Fact]
    public void A_cleanup_reads_the_known_stream_files_again()
    {
        string iptv = Directory.CreateDirectory(Path.Combine(_root, "IPTV")).FullName;
        string config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        File.WriteAllText(Path.Combine(config, "Jellyfin.Xtream.Library.xml"), $"<PluginConfiguration><LibraryPath>{iptv}</LibraryPath></PluginConfiguration>");
        var paths = FakePaths.At(config, Path.Combine(_root, "log"));

        Housekeeping.KnownStrmPaths(_library.Manager, CancellationToken.None);
        Housekeeping.Clean("orphan-strm:" + iptv, paths, Path.Combine(_root, "transcodes"), () => Housekeeping.KnownStrmPaths(_library.Manager, CancellationToken.None));

        Assert.Equal(2, _library.Listings);
    }

    [Fact]
    public void A_cancelled_request_stops_reading_the_known_stream_files()
    {
        using var cts = new CancellationTokenSource();
        var library = CountingLibrary.Listing(() => CancelOnFirst(cts, Path.Combine(_root, "IPTV")));

        Assert.ThrowsAny<OperationCanceledException>(() => Housekeeping.KnownStrmPaths(library.Manager, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Housekeeping.KnownStrmPaths(library.Manager, cts.Token));
    }

    public void Dispose()
    {
        Housekeeping.KnownStrm.Clear();
        TrackCleaner.KeptOriginalsWalk.Clear();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>A thousand stream files, cancelling the request once the first is read, as a closed tab would.</summary>
    private static IEnumerable<BaseItem> CancelOnFirst(CancellationTokenSource cts, string folder)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (i == 1)
            {
                cts.Cancel();
            }

            yield return new Movie { Path = Path.Combine(folder, $"Item {i}.strm") };
        }
    }

    /// <summary>A library manager that only lists items, and counts how often it was asked.</summary>
    private sealed class CountingLibrary
    {
        private int _listings;

        public ILibraryManager Manager { get; private set; } = null!;

        public int Listings => Volatile.Read(ref _listings);

        public static CountingLibrary Listing(Func<IEnumerable<BaseItem>> list)
        {
            var counting = new CountingLibrary();
            counting.Manager = Proxy.Create(counting, list);
            return counting;
        }

        public class Proxy : DispatchProxy
        {
            private CountingLibrary _owner = null!;
            private Func<IEnumerable<BaseItem>> _list = Array.Empty<BaseItem>;

            public static ILibraryManager Create(CountingLibrary owner, Func<IEnumerable<BaseItem>> list)
            {
                var manager = Create<ILibraryManager, Proxy>();
                var proxy = (Proxy)(object)manager;
                proxy._owner = owner;
                proxy._list = list;
                return manager;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name != nameof(ILibraryManager.GetItemList))
                {
                    throw new NotSupportedException(targetMethod?.Name);
                }

                Interlocked.Increment(ref _owner._listings);
                return new Lazily(_list());
            }
        }

        /// <summary>Hands the items out one at a time, so a walk that stops early never reads the rest.</summary>
        private sealed class Lazily(IEnumerable<BaseItem> items) : IReadOnlyList<BaseItem>
        {
            public int Count => throw new NotSupportedException();

            public BaseItem this[int index] => throw new NotSupportedException();

            public IEnumerator<BaseItem> GetEnumerator() => items.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>Jellyfin's folders, of which a cleanup only reads these two.</summary>
    private class FakePaths : DispatchProxy
    {
        private string _config = string.Empty;
        private string _log = string.Empty;

        public static IApplicationPaths At(string config, string log)
        {
            var paths = Create<IApplicationPaths, FakePaths>();
            var fake = (FakePaths)(object)paths;
            fake._config = config;
            fake._log = log;
            return paths;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_" + nameof(IApplicationPaths.PluginConfigurationsPath) => _config,
            "get_" + nameof(IApplicationPaths.LogDirectoryPath) => _log,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }
}

using System.Collections;
using System.Reflection;
using JellyfinMedic.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JellyfinMedic.Tests;

/// <summary>A track run's progress and its Stop are shared by the whole server, so these tests run on their own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TrackRunCollection
{
    public const string Name = "Track runs";
}

[Collection(TrackRunCollection.Name)]
public sealed class TrackRunTests : IDisposable
{
    // A run here takes milliseconds; this only keeps a broken one from hanging the tests.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // A library whose ID isn't a Guid is skipped before the library manager is asked for its files.
    private static readonly ILibraryManager NoLibraryManager = null!;

    private static readonly LibraryFacts Films = new() { Id = Guid.NewGuid().ToString("N"), Name = "Films" };

    private readonly string _root = Directory.CreateTempSubdirectory("medic-tests-").FullName;
    private int _handedOut;

    [Fact]
    public async Task Starting_a_run_returns_before_its_libraries_are_listed()
    {
        using var gate = new ManualResetEventSlim();

        // On a thread of its own, so a call that blocks can't hold up the test.
        var start = Task.Factory.StartNew(
            () => TrackCleaner.RunAsync(NoLibraryManager, LibrariesAfter(gate), string.Empty, new PluginConfiguration(), dryRun: true),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            bool returned = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(5))) == start;

            Assert.True(returned, "Starting a run waited for its libraries to be listed.");
            Assert.True(TrackCleaner.CurrentProgress().Running);
        }
        finally
        {
            gate.Set();
            await start.Unwrap().WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_ends_a_run_while_its_libraries_are_listed(bool dryRun)
    {
        var libraries = StopAfterFirst(1000, i => new LibraryFacts { Id = $"library {i}" });

        // A real run only starts when FFprobe is beside FFmpeg.
        await TrackCleaner.RunAsync(NoLibraryManager, libraries, FakeFfmpeg(), new PluginConfiguration(), dryRun).WaitAsync(Limit);

        Assert.InRange(_handedOut, 1, 999);
        Assert.False(TrackCleaner.CurrentProgress().Running);
    }

    [Fact]
    public async Task Stop_ends_a_run_while_the_files_of_a_library_are_planned()
    {
        // Files that don't exist, so planning never asks Jellyfin for their tracks.
        var library = FakeLibrary.Listing(() => new OneByOne<BaseItem>(StopAfterFirst(1000, i => new Movie { Path = Path.Combine(_root, $"Film {i}.mkv") })));

        await TrackCleaner.RunAsync(library, new[] { Films }, string.Empty, new PluginConfiguration(), dryRun: true).WaitAsync(Limit);

        Assert.InRange(_handedOut, 1, 999);
        Assert.False(TrackCleaner.CurrentProgress().Running);
    }

    [UnixFact]
    public async Task A_save_during_a_run_does_not_switch_it_from_keeping_originals_to_replacing_them()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "Films")).FullName;
        string film = Path.Combine(folder, "Film.mkv");
        File.WriteAllText(film, "a film with English and French audio");
        var settings = new PluginConfiguration { TracksReplaceInPlace = false };
        var library = FakeLibrary.Listing(() =>
        {
            settings.TracksReplaceInPlace = true; // saved while the run lists its files
            return new BaseItem[] { new Film { Path = film } };
        });

        await TrackCleaner.RunAsync(library, new[] { Films }, FakeFfmpeg(), settings, dryRun: false).WaitAsync(Limit);

        Assert.True(
            File.Exists(Path.Combine(folder, ".medic-originals", "Film.mkv")),
            "The original wasn't kept. The run's notes: " + string.Join(" | ", TrackCleaner.CurrentProgress().Recent));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // One library, handed out once the gate opens, like a library that's slow to list.
    private static IEnumerable<LibraryFacts> LibrariesAfter(ManualResetEventSlim gate)
    {
        gate.Wait();
        yield return new LibraryFacts { Id = "films" };
    }

    /// <summary>Hands out count things, pressing Stop when the second is asked for, and counts what it handed out.</summary>
    private IEnumerable<T> StopAfterFirst<T>(int count, Func<int, T> make)
    {
        for (int i = 0; i < count; i++)
        {
            if (i == 1)
            {
                TrackCleaner.Stop();
            }

            _handedOut++;
            yield return make(i);
        }
    }

    /// <summary>FFmpeg and FFprobe stand-ins in a folder of their own. Returns FFmpeg's path.</summary>
    private string FakeFfmpeg()
    {
        // FFprobe's report on the film, and on its cleaned copy, which has lost the French track. Asked for the copy's
        // last packets, it says they reach the 60 s its report gives.
        const string ffprobe = """
            #!/bin/sh
            case "$*" in *-read_intervals*) echo '59.960000,0.040000'; exit ;; esac
            for last; do :; done
            case "$last" in
              *.medic-tmp*) french='' ;;
              *) french=',{"index":2,"codec_type":"audio","tags":{"language":"fre"}}' ;;
            esac
            echo '{"streams":[{"index":0,"codec_type":"video"},{"index":1,"codec_type":"audio","tags":{"language":"eng"}}'"$french"'],"format":{"duration":"60"}}'
            """;

        // FFmpeg copies the film (the file after -i) to the new copy (the last argument).
        const string ffmpeg = """
            #!/bin/sh
            while [ $# -gt 1 ]; do
              [ "$1" = -i ] && input=$2
              shift
            done
            cp "$input" "$1"
            """;

        string bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        Script(Path.Combine(bin, "ffprobe"), ffprobe);
        Script(Path.Combine(bin, "ffmpeg"), ffmpeg);
        return Path.Combine(bin, "ffmpeg");
    }

    private static void Script(string path, string text)
    {
        File.WriteAllText(path, text);

        // Windows has no execute permission to give; the test that runs these is skipped there.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A library manager that only lists items, which is all a track run asks of it.</summary>
    private class FakeLibrary : DispatchProxy
    {
        private Func<IReadOnlyList<BaseItem>> _list = Array.Empty<BaseItem>;

        public static ILibraryManager Listing(Func<IReadOnlyList<BaseItem>> list)
        {
            var library = Create<ILibraryManager, FakeLibrary>();
            ((FakeLibrary)(object)library)._list = list;
            return library;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ILibraryManager.GetItemList) ? _list() : throw new NotSupportedException(targetMethod?.Name);
    }

    /// <summary>A list that hands out its items one at a time, as the run only walks it.</summary>
    private sealed class OneByOne<T>(IEnumerable<T> items) : IReadOnlyList<T>
    {
        public int Count => throw new NotSupportedException();

        public T this[int index] => throw new NotSupportedException();

        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A film with an English and a French audio track, as Jellyfin lists them.</summary>
    private sealed class Film : Movie
    {
        public override IReadOnlyList<MediaStream> GetMediaStreams() => new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new MediaStream { Index = 2, Type = MediaStreamType.Audio, Language = "fre" }
        };

        // Asks Jellyfin to read the file again, and there is no Jellyfin here.
        public override void ChangedExternally()
        {
        }
    }
}

/// <summary>A test whose FFmpeg and FFprobe stand-ins are shell scripts, so it is skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "The FFmpeg and FFprobe stand-ins are shell scripts.";
        }
    }
}

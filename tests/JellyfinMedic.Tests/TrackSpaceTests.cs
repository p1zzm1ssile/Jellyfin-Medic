using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class TrackSpaceTests : IDisposable
{
    private const long GiB = 1L << 30;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_floor_is_10_GiB_or_5_percent_of_the_drive()
    {
        Assert.Equal(10 * GiB, TrackCleaner.SpaceFloor(100 * GiB));
        Assert.Equal(50 * GiB, TrackCleaner.SpaceFloor(1000 * GiB));
        Assert.Equal(10 * GiB, TrackCleaner.SpaceFloor(-1));
    }

    [Fact]
    public void A_run_stops_short_of_the_floor_rather_than_filling_the_drive()
    {
        long writing = 0;

        // 4 GiB file: the copy, a 1 GiB margin and the 10 GiB floor need 15 GiB.
        Assert.True(TrackCleaner.Reserve(15 * GiB, 100 * GiB, 4 * GiB, ref writing, out _));
        Assert.Equal(4 * GiB, writing);

        // A second file now counts the first one's copy still being written.
        Assert.False(TrackCleaner.Reserve(15 * GiB, 100 * GiB, 4 * GiB, ref writing, out long needed));
        Assert.Equal(9 * GiB, needed);
        Assert.Equal(4 * GiB, writing);
    }

    [Fact]
    public void Unknown_free_space_doesnt_hold_a_file_back()
    {
        long writing = 0;
        Assert.True(TrackCleaner.Reserve(-1, -1, 4 * GiB, ref writing, out _));
    }

    [Fact]
    public async Task Releases_racing_reservations_all_come_off()
    {
        var writing = new TrackCleaner.SpaceReservations();
        const int Workers = 4, Rounds = 200_000;

        // As many files at once as a run allows, each reserving and releasing over and over.
        using var start = new Barrier(Workers);
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                Assert.True(writing.TryReserve(GiB, () => (-1L, -1L), out _, out _, out _));
                writing.Release(GiB);
            }
        })));

        Assert.Equal(0, writing.Bytes);
    }

    [Fact]
    public async Task A_reservation_reads_free_space_only_after_the_one_before_it_is_made()
    {
        var writing = new TrackCleaner.SpaceReservations();
        using var firstReading = new ManualResetEventSlim();
        using var secondReading = new ManualResetEventSlim();
        bool overlapped = false;

        var first = Task.Run(() => writing.TryReserve(4 * GiB, () =>
        {
            firstReading.Set();
            // Holds the read open: a second file reading the drive meanwhile would decide on a figure
            // that doesn't count this reservation's copy.
            overlapped = secondReading.Wait(TimeSpan.FromMilliseconds(500));
            return (15 * GiB, 100 * GiB);
        }, out _, out _, out _));
        firstReading.Wait();

        bool second = writing.TryReserve(4 * GiB, () =>
        {
            secondReading.Set();
            return (15 * GiB, 100 * GiB);
        }, out _, out _, out long needed);

        bool firstReserved = await first;
        Assert.False(overlapped);
        Assert.True(firstReserved);
        Assert.False(second);
        Assert.Equal(9 * GiB, needed);
        Assert.Equal(4 * GiB, writing.Bytes);
    }

    [Fact]
    public void A_new_run_starts_with_nothing_reserved()
    {
        var writing = new TrackCleaner.SpaceReservations();
        Assert.True(writing.TryReserve(4 * GiB, () => (15 * GiB, 100 * GiB), out _, out _, out _));

        writing.Reset();

        Assert.Equal(0, writing.Bytes);
        Assert.True(writing.TryReserve(4 * GiB, () => (15 * GiB, 100 * GiB), out _, out _, out _));
    }

    [Fact]
    public void An_earlier_releases_original_is_moved_aside_under_a_dated_name()
    {
        string kept = Path.Combine("films", ".medic-originals", "Film (2020).mkv");

        Assert.Equal(Path.Combine("films", ".medic-originals", "Film (2020).replaced-20261017-093005.mkv"),
            TrackCleaner.ReplacedPath(kept, new DateTime(2026, 10, 17, 9, 30, 5)));
    }

    [Fact]
    public void A_kept_original_without_its_file_is_an_orphan_unless_its_swap_was_cut_short()
    {
        string originals = Directory.CreateDirectory(Path.Combine(_root, ".medic-originals")).FullName;
        string Kept(string name)
        {
            string path = Path.Combine(originals, name);
            File.WriteAllText(path, "x");
            return path;
        }

        string inPlace = Kept("In place.mkv");
        File.WriteAllText(Path.Combine(_root, "In place.mkv"), "x");
        string renamed = Kept("Renamed.mkv");
        string cutShort = Kept("Cut short.mkv");
        File.WriteAllText(Path.Combine(_root, "Cut short.mkv.medic-tmp.mkv"), "x");

        Assert.False(TrackCleaner.IsOrphan(_root, inPlace));
        Assert.True(TrackCleaner.IsOrphan(_root, renamed));
        Assert.False(TrackCleaner.IsOrphan(_root, cutShort));
    }
}

[Collection(TrackRunCollection.Name)]
public sealed class TrackChangesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-tests-").FullName;
    private readonly string? _changesFile = TrackCleaner.ChangesFile;

    public void Dispose()
    {
        TrackCleaner.ChangesFile = _changesFile;
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void An_unreadable_changes_file_lists_nothing_rather_than_failing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string file = Path.Combine(_root, "track_changes.jsonl");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            new TrackCleaner.TrackChange(DateTime.UtcNow, "/films/Film.mkv", "1 audio", 2, 1, null)) + "\n");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            File.OpenRead(file).Dispose();
            return; // root reads it anyway
        }
        catch (UnauthorizedAccessException)
        {
        }

        TrackCleaner.ChangesFile = file;

        Assert.Empty(TrackCleaner.RecentChanges(200));
        TrackCleaner.RecentChanges(200);

        // Each look at the list reads the file again; the run notes say it once rather than filling up with it.
        Assert.Single(TrackCleaner.CurrentProgress().Recent, l => l.Contains("Couldn't read the record of changed files", StringComparison.Ordinal));
    }
}

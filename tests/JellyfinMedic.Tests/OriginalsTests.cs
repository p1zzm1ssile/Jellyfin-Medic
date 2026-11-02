using System.Diagnostics;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class OriginalsTests : IDisposable
{
    private const UnixFileMode Shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-originals-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    private static string Owner(string path) => Run("stat", "-c", "%u:%g", "--", path);

    private string Film(UnixFileMode folderMode)
    {
        string folder = Path.Combine(_root, "Film (2020)");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            new DirectoryInfo(folder).UnixFileMode = folderMode;
        }

        string film = Path.Combine(folder, "Film (2020).mkv");
        File.WriteAllText(film, "original");
        return film;
    }

    [Fact]
    public async Task The_originals_folder_takes_its_parents_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = await TrackCleaner.EnsureOriginalsFolder(Film(Shared), CancellationToken.None);

        Assert.Equal(Shared, new DirectoryInfo(folder).UnixFileMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            File.GetUnixFileMode(Path.Combine(folder, ".ignore")));
    }

    [Fact]
    public async Task The_originals_folder_keeps_its_parents_setgid_bit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = await TrackCleaner.EnsureOriginalsFolder(Film(Shared | UnixFileMode.SetGroup), CancellationToken.None);

        Assert.Equal(Shared | UnixFileMode.SetGroup, new DirectoryInfo(folder).UnixFileMode);
    }

    [Fact]
    public async Task The_originals_folder_and_its_ignore_file_have_the_parents_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string film = Film(Shared);
        string folder = await TrackCleaner.EnsureOriginalsFolder(film, CancellationToken.None);

        string parent = Owner(Path.GetDirectoryName(film)!);
        Assert.Equal(parent, Owner(folder));
        Assert.Equal(parent, Owner(Path.Combine(folder, ".ignore")));
    }

    [Fact]
    public async Task When_the_owner_cant_be_given_the_group_still_is()
    {
        if (OperatingSystem.IsWindows() || Run("id", "-u") == "0")
        {
            return; // root may give any owner, so the fallback never runs
        }

        string file = Film(Shared);
        uint gid = uint.Parse(Run("id", "-g"), System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(await TrackCleaner.MatchOwner(file, 0, gid, CancellationToken.None));
        Assert.Equal(Run("id", "-u") + ":" + gid, Owner(file));
        Assert.False(await TrackCleaner.MatchOwner(file, 0, 0, CancellationToken.None));
    }

    private static TrackCleaner.ProbedFile Probed(int video, int audio, double? seconds) =>
        new(Enumerable.Repeat((0, "video", "und"), video).Concat(Enumerable.Repeat((0, "audio", "eng"), audio)).ToList(), seconds);

    [Fact]
    public void A_leftover_copy_that_reads_like_the_original_may_take_its_place()
    {
        Assert.Null(TrackCleaner.CheckLeftover(Probed(1, 2, 100), Probed(1, 1, 99), 1000, 900));
        Assert.Null(TrackCleaner.CheckLeftover(Probed(1, 2, null), Probed(1, 1, null), 900, 400));
        Assert.Null(TrackCleaner.CheckLeftover(null, Probed(1, 1, 100), 0, 900));
    }

    [Fact]
    public void A_leftover_copy_that_doesnt_read_like_the_original_is_refused()
    {
        Assert.Equal("FFprobe couldn't read it", TrackCleaner.CheckLeftover(Probed(1, 2, 100), null, 1000, 900));
        Assert.Equal("it has no audio", TrackCleaner.CheckLeftover(Probed(1, 2, 100), Probed(1, 0, 100), 1000, 900));
        Assert.Equal("it has 0 video streams instead of 1", TrackCleaner.CheckLeftover(Probed(1, 2, 100), Probed(0, 1, 100), 1000, 900));
        Assert.Equal("it lasts 50 s instead of 100 s", TrackCleaner.CheckLeftover(Probed(1, 2, 100), Probed(1, 1, 50), 1000, 900));
        Assert.Equal("it's much smaller than the original and its length can't be read",
            TrackCleaner.CheckLeftover(Probed(1, 2, 100), Probed(1, 1, null), 900, 200));
        Assert.Equal("it has no video", TrackCleaner.CheckLeftover(null, Probed(0, 1, 100), 0, 900));
    }

    [Fact]
    public async Task Tidy_leaves_a_copy_that_fails_its_check_and_the_original_as_they_are()
    {
        string film = Film(Shared);
        string copy = Path.ChangeExtension(film, ".medic-stripped.mkv");
        File.WriteAllText(copy, "broken");

        var result = await TrackCleaner.TidyFolder(Path.GetDirectoryName(film)!, _ => Task.FromResult<TrackCleaner.ProbedFile?>(null), false, CancellationToken.None);

        Assert.Equal((0, 0, 0L), (result.Titles, result.CopiesDeleted, result.Freed));
        Assert.Equal(new[] { new TidyLeft("Film (2020).mkv", "FFprobe couldn't read it") }, result.Left);
        Assert.Equal("original", File.ReadAllText(film));
        Assert.Equal("broken", File.ReadAllText(copy));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(film)!, ".medic-originals")));
        Assert.Contains(TrackCleaner.CurrentProgress().Recent, r => r.Contains("Left Film (2020).mkv and its earlier copy as they are: the copy didn't check out (FFprobe couldn't read it)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tidy_puts_a_copy_that_checks_out_in_the_originals_place()
    {
        string film = Film(Shared);
        string copy = Path.ChangeExtension(film, ".medic-stripped.mkv");
        File.WriteAllText(copy, "cleaned");

        var result = await TrackCleaner.TidyFolder(Path.GetDirectoryName(film)!, _ => Task.FromResult<TrackCleaner.ProbedFile?>(Probed(1, 1, 100)), false, CancellationToken.None);

        Assert.Equal((1, 0, 0L), (result.Titles, result.CopiesDeleted, result.Freed));
        Assert.Empty(result.Left);
        Assert.Equal("cleaned", File.ReadAllText(film));
        Assert.False(File.Exists(copy));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Path.GetDirectoryName(film)!, ".medic-originals", "Film (2020).mkv")));
    }
}

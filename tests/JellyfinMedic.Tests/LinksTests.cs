using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

// Hard links come from ln, and BSD's stat is played by a shell script.
[UnsupportedOSPlatform("windows")]
public sealed class LinksTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    [Fact]
    public void Track_cleanup_leaves_a_symlinked_file_alone()
    {
        Assert.Contains("symbolic link", TrackCleaner.LinkSkipReason((1, 1000, 100), isSymlink: true, countLinks: true));
    }

    [Fact]
    public void Track_cleanup_leaves_a_file_alone_when_its_links_cant_be_counted()
    {
        Assert.Contains("couldn't be counted", TrackCleaner.LinkSkipReason(null, isSymlink: false, countLinks: true));
    }

    [Fact]
    public void Track_cleanup_leaves_a_hard_linked_file_alone()
    {
        Assert.Contains("2 hard links", TrackCleaner.LinkSkipReason((2, 1000, 100), isSymlink: false, countLinks: true));
    }

    [Fact]
    public void Track_cleanup_goes_ahead_with_a_plain_file_with_one_link()
    {
        Assert.Null(TrackCleaner.LinkSkipReason((1, 1000, 100), isSymlink: false, countLinks: true));
    }

    // Where links aren't counted (Windows), FileFacts gives null by design.
    [Fact]
    public void Track_cleanup_leaves_a_symlinked_file_alone_where_links_arent_counted()
    {
        Assert.Contains("symbolic link", TrackCleaner.LinkSkipReason(null, isSymlink: true, countLinks: false));
    }

    [Fact]
    public void Track_cleanup_goes_ahead_without_facts_where_links_arent_counted()
    {
        Assert.Null(TrackCleaner.LinkSkipReason(null, isSymlink: false, countLinks: false));
    }

    [Fact]
    public void Stat_output_gives_the_link_count_owner_and_group()
    {
        // GNU and busybox print this for -c "%h %u %g", BSD and macOS for -f "%l %u %g".
        Assert.Equal((2, 1000u, 100u), Assert.NotNull(TrackCleaner.ParseStat("2 1000 100")));
    }

    [Fact]
    public void An_owner_beyond_the_signed_range_is_still_read()
    {
        // nfsnobody on older NFS servers.
        Assert.Equal((1, 4294967294u, 4294967294u), Assert.NotNull(TrackCleaner.ParseStat("1 4294967294 4294967294")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2 1000")]
    [InlineData("2 1000 100 4096")]
    [InlineData("two 1000 100")]
    public void Stat_output_that_cant_be_read_gives_nothing(string output)
    {
        Assert.Null(TrackCleaner.ParseStat(output));
    }

    [Fact]
    public async Task A_hard_linked_file_counts_two_links()
    {
        string film = Write(Path.Combine(_paths.Root, "Film.mkv"));
        await Run("ln", film, Path.Combine(_paths.Root, "Seeding.mkv"));

        var facts = Assert.NotNull(await TrackCleaner.FileFacts(film, CancellationToken.None));

        Assert.Equal(2, facts.Links);
    }

    [Fact]
    public async Task Bsd_stat_is_asked_with_f_once_it_refuses_c()
    {
        string film = Write(Path.Combine(_paths.Root, "Film.mkv"));
        string stat = Path.Combine(_paths.Root, "stat");
        File.WriteAllText(stat, string.Join('\n', "#!/bin/sh", "[ \"$1\" = -f ] && [ \"$2\" = '%l %u %g' ] && echo '2 1000 100' && exit 0", "exit 1", string.Empty));
        File.SetUnixFileMode(stat, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var facts = Assert.NotNull(await TrackCleaner.FileFacts(film, stat, CancellationToken.None));

        Assert.Equal((2, 1000u, 100u), facts);
    }

    [Fact]
    public void Housekeeping_lists_nothing_reached_through_a_link()
    {
        string transcodes = Folder("transcodes");
        Old(Path.Combine(Folder("elsewhere"), "old.ts"), TimeSpan.FromDays(1));
        Directory.CreateSymbolicLink(Path.Combine(transcodes, "link"), Path.Combine(_paths.Root, "elsewhere"));

        var report = Housekeeping.Scan(_paths, transcodes, () => null);

        Assert.DoesNotContain(report.Items, i => i.Kind == "transcodes");
    }

    [Fact]
    public void Housekeeping_leaves_files_reached_through_a_link()
    {
        string transcodes = Folder("transcodes");
        string old = Old(Path.Combine(Folder("elsewhere"), "old.ts"), TimeSpan.FromDays(1));
        Directory.CreateSymbolicLink(Path.Combine(transcodes, "link"), Path.Combine(_paths.Root, "elsewhere"));

        Housekeeping.Clean("transcodes", _paths, transcodes, () => null);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void Housekeeping_still_cleans_an_old_file_in_a_real_subfolder()
    {
        string transcodes = Folder("transcodes");
        string old = Old(Path.Combine(Folder("transcodes", "sub"), "old.ts"), TimeSpan.FromDays(1));

        var (ok, _, _) = Housekeeping.Clean("transcodes", _paths, transcodes, () => null);

        Assert.True(ok);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Housekeeping_still_cleans_a_transcode_folder_that_is_itself_a_link()
    {
        string old = Old(Path.Combine(Folder("cache-transcodes"), "old.ts"), TimeSpan.FromDays(1));
        string transcodes = Path.Combine(_paths.Root, "transcodes");
        Directory.CreateSymbolicLink(transcodes, Path.Combine(_paths.Root, "cache-transcodes"));

        Assert.Equal(1, Assert.Single(Housekeeping.Scan(_paths, transcodes, () => null).Items, i => i.Kind == "transcodes").Count);
        Housekeeping.Clean("transcodes", _paths, transcodes, () => null);

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Housekeeping_keeps_an_empty_folder_reached_through_a_link()
    {
        string strm = Folder("xtream");
        Directory.CreateDirectory(_paths.PluginConfigurationsPath);
        new XElement("PluginConfiguration", new XElement("LibraryPath", strm)).Save(Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Xtream.Library.xml"));
        string known = Write(Path.Combine(strm, "Known.strm"));
        string gone = Old(Path.Combine(strm, "Gone.strm"), TimeSpan.FromDays(3));
        string empty = Folder("elsewhere", "empty");
        Directory.CreateSymbolicLink(Path.Combine(strm, "link"), Path.Combine(_paths.Root, "elsewhere"));

        var (ok, _, _) = Housekeeping.Clean("orphan-strm:" + strm, _paths, Path.Combine(_paths.Root, "transcodes"), () => new HashSet<string> { known });

        Assert.True(ok);
        Assert.False(File.Exists(gone));
        Assert.True(Directory.Exists(empty));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private static async Task Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        string errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{tool} failed: {errors}");
    }

    private static string Write(string path)
    {
        File.WriteAllText(path, "media");
        return path;
    }

    // Ages are read in UTC, so the time zone doesn't matter.
    private static string Old(string path, TimeSpan age)
    {
        Write(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private string Folder(params string[] parts) =>
        Directory.CreateDirectory(Path.Combine(_paths.Root, Path.Combine(parts))).FullName;
}

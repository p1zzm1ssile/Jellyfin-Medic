using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class DriveSpaceTests : IDisposable
{
    // Medic reads sizes in GiB, so a 16 TB drive is about 14,901 of them.
    private const double SixteenTb = 14901;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-drives-").FullName;

    public void Dispose()
    {
        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            if (!OperatingSystem.IsWindows())
            {
                new DirectoryInfo(folder).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }
        }

        Directory.Delete(_root, true);
    }

    private string Folder(string name, bool withFile)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        if (withFile)
        {
            File.WriteAllText(Path.Combine(folder, "Film (2020).mkv"), "film");
        }

        return folder;
    }

    // A reading of the drive behind folder, as Record saves it.
    private static DiskSample ReadingOf(string folder, string holds) => new() { Key = "/mnt/films", Day = "2026-10-01", FreeGb = 100, Holds = holds, Path = folder };

    [Theory]
    [InlineData(140, "Improve")]
    [InlineData(200, null)]
    [InlineData(49, "Problem")]
    [InlineData(50, "Improve")] // the full limit itself is not full
    [InlineData(150, null)] // nor is the nearly full one nearly full
    public void A_big_drive_is_judged_by_gigabytes_not_by_a_share_of_its_size(double freeGb, string? expected)
    {
        Assert.Equal(expected, DiagnosticsEngine.SpaceVerdict(freeGb, SixteenTb, "Films"));
    }

    [Theory]
    [InlineData(8, 931, "Problem")]
    [InlineData(20, 931, "Improve")]
    [InlineData(100, 931, null)]
    [InlineData(4.5, 300, "Problem")]
    [InlineData(5, 300, "Improve")]
    [InlineData(20, 300, "Improve")]
    [InlineData(39, 4000, "Problem")]
    [InlineData(110, 4000, "Improve")]
    [InlineData(130, 4000, null)]
    public void Drives_up_to_about_5_TB_keep_the_same_limits(double freeGb, double totalGb, string? expected)
    {
        Assert.Equal(expected, DiagnosticsEngine.SpaceVerdict(freeGb, totalGb, "Films"));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(12, 16)]
    public void A_drive_holding_only_transcodes_is_never_full(double freeGb, double totalGb)
    {
        Assert.Null(DiagnosticsEngine.SpaceVerdict(freeGb, totalGb, "transcodes"));
    }

    [Fact]
    public void A_drive_sharing_transcodes_with_a_library_is_still_checked()
    {
        Assert.Equal("Problem", DiagnosticsEngine.SpaceVerdict(4, 1000, "transcodes, Films"));
    }

    [Fact]
    public void A_drive_holding_only_transcodes_never_will_be_full()
    {
        // One reading taken during a transcode looks like the drive filling up.
        var drive = new DriveStatus { Key = "/transcode", MountPoint = "/transcode", Holds = "transcodes", FreeGb = 1, TotalGb = 4, GbPerDay = 1 };

        Assert.Empty(DiagnosticsEngine.DriveSpaceFindings(new[] { drive }));
    }

    [Fact]
    public void A_library_drive_filling_up_is_still_reported()
    {
        var drive = new DriveStatus { Key = "/mnt/films", MountPoint = "/mnt/films", Holds = "Films", FreeGb = 300, TotalGb = 931, GbPerDay = 20 };

        var finding = Assert.Single(DiagnosticsEngine.DriveSpaceFindings(new[] { drive }));
        Assert.Equal("The drive for Films will be full in about 2 weeks", finding.Title);
    }

    [Theory]
    [InlineData(0.3, "about 1 day")]
    [InlineData(1, "about 1 day")]
    [InlineData(3, "about 3 days")]
    [InlineData(21, "about 3 weeks")]
    public void Time_to_full_reads_properly(double days, string expected)
    {
        Assert.Equal(expected, DiagnosticsEngine.Weeks(days));
    }

    [Fact]
    public void Two_folders_on_drives_Medic_cant_name_count_as_different_drives()
    {
        Assert.False(DiagnosticsEngine.SameDrive(null, null));
        Assert.False(DiagnosticsEngine.SameDrive("/", null));
    }

    [Fact]
    public void Two_folders_on_one_named_drive_are_the_same_drive()
    {
        Assert.True(DiagnosticsEngine.SameDrive("/", "/"));
        Assert.False(DiagnosticsEngine.SameDrive("/", "/mnt/cache"));
    }

    [Fact]
    public void Without_mount_points_the_cache_on_another_volume_is_another_drive()
    {
        var roots = new Lazy<List<string>>(() => new List<string> { @"C:\", @"D:\" });
        string? cache = StorageWatch.DriveOf(@"D:\Jellyfin\cache", roots, _ => null);
        string? data = StorageWatch.DriveOf(@"C:\ProgramData\Jellyfin\Server", roots, _ => null);

        Assert.Equal(@"D:\", cache);
        Assert.False(DiagnosticsEngine.SameDrive(cache, data));
    }

    [Fact]
    public void A_missing_or_empty_library_folder_is_not_counted_as_its_parents_drive()
    {
        string parent = Path.GetTempPath();
        var drives = StorageWatch.Drives(new[] { ("Films", Path.Combine(parent, "medic-not-mounted")) }, _ => false);

        Assert.Empty(drives);
    }

    [Fact]
    public void Jellyfins_own_folders_are_read_through_their_parent_when_not_made_yet()
    {
        string parent = Path.GetTempPath();
        var drive = Assert.Single(StorageWatch.Drives(new[] { ("transcodes", Path.Combine(parent, "medic-no-transcodes-yet")) }, _ => false));

        Assert.Equal("transcodes", drive.Holds);
    }

    [Fact]
    public void A_library_folder_reads_as_listed_empty_or_gone()
    {
        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Listed, DiagnosticsEngine.ReadLibraryFolder(Folder("films", true)).State);
        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Empty, DiagnosticsEngine.ReadLibraryFolder(Folder("new", false)).State);
        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Gone, DiagnosticsEngine.ReadLibraryFolder(Path.Combine(_root, "never-made")).State);
    }

    [Fact]
    public void A_library_folder_that_fails_to_list_is_read_by_its_error()
    {
        // No test can make a share stop answering, so the listing throws what one would.
        var read = DiagnosticsEngine.ReadLibraryFolder("/mnt/share/films", _ => throw new IOException("Stale file handle"));

        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Failed, read.State);
        Assert.Equal("Stale file handle", read.Error);
    }

    [Fact]
    public void A_listing_refused_is_denied_and_a_folder_not_found_is_gone()
    {
        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Denied,
            DiagnosticsEngine.ReadLibraryFolder("/mnt/films", _ => throw new UnauthorizedAccessException("Access denied")).State);
        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Gone,
            DiagnosticsEngine.ReadLibraryFolder("/mnt/films", _ => throw new DirectoryNotFoundException("Not found")).State);
    }

    [Fact]
    public void A_library_folder_Jellyfin_may_not_list_is_reported()
    {
        // Root lists any folder, and Windows has no Unix permissions.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        string folder = Folder("locked", true);
        new DirectoryInfo(folder).UnixFileMode = UnixFileMode.None;

        Assert.Equal(DiagnosticsEngine.LibraryFolderState.Denied, DiagnosticsEngine.ReadLibraryFolder(folder).State);
        var finding = Assert.Single(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("Films", folder) }, Array.Empty<DiskSample>(), DiagnosticsEngine.ReadLibraryFolder));
        Assert.Equal("The drive for Films can't be read", finding.Title);
        Assert.Equal("Jellyfin isn't allowed to list its folder", finding.Current);
        Assert.Equal("Problem", finding.Severity);
        Assert.Equal(folder, finding.Where);
    }

    [Fact]
    public void A_library_folder_that_fails_to_list_gives_the_error_rather_than_a_cause()
    {
        // A network share that stopped answering fails with an I/O error, not a permission one; no test can make one.
        string folder = Path.Combine(_root, "share");
        static (DiagnosticsEngine.LibraryFolderState, string) Failing(string path) => (DiagnosticsEngine.LibraryFolderState.Failed, "Stale file handle");

        var finding = Assert.Single(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("Films", folder) }, Array.Empty<DiskSample>(), Failing));
        Assert.Equal("The drive for Films couldn't be read", finding.Title);
        Assert.Equal("Listing its folder failed: Stale file handle", finding.Current);
        Assert.DoesNotContain("allowed", finding.Current + finding.Recommended, StringComparison.Ordinal);
        Assert.Equal(folder, finding.Where);
    }

    [Fact]
    public void A_new_empty_library_is_not_reported()
    {
        string folder = Folder("new", false);

        Assert.Empty(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("New films", folder) }, Array.Empty<DiskSample>(), DiagnosticsEngine.ReadLibraryFolder));
    }

    [Fact]
    public void An_empty_library_folder_whose_drive_was_read_before_is_reported()
    {
        // A drive that didn't mount leaves its mount point empty; the drive's last reading went through a folder inside it.
        string folder = Folder("films", false);
        var history = new[] { ReadingOf(Path.Combine(folder, "Films"), "Films, Shows") };

        var finding = Assert.Single(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("Films", folder) }, history, DiagnosticsEngine.ReadLibraryFolder));
        Assert.Equal("The drive for Films looks unmounted", finding.Title);
        Assert.Equal("Problem", finding.Severity);
        Assert.Equal(folder, finding.Where);
    }

    [Fact]
    public void An_empty_folder_beside_one_read_before_is_new_rather_than_unmounted()
    {
        string folder = Folder("films", false);
        var history = new[] { ReadingOf(Folder("films-old", true), "Films") };

        Assert.Empty(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("Films", folder) }, history, DiagnosticsEngine.ReadLibraryFolder));
    }

    [Fact]
    public void Listed_and_gone_library_folders_have_no_storage_finding()
    {
        // A gone folder has its own finding under Libraries.
        string listed = Folder("films", true);
        string gone = Path.Combine(_root, "never-made");
        var history = new[] { ReadingOf(listed, "Films"), ReadingOf(gone, "Shows") };

        Assert.Empty(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("Films", listed), ("Shows", gone) }, history, DiagnosticsEngine.ReadLibraryFolder));
    }

    [Fact]
    public void One_library_with_two_unmounted_folders_is_one_finding()
    {
        string first = Folder("disk1", false);
        string second = Folder("disk2", false);
        var history = new[] { ReadingOf(first, "Films"), ReadingOf(second, "Films") };

        var finding = Assert.Single(DiagnosticsEngine.UnreadLibraryFindings(new[] { ("Films", first), ("Films", second) }, history, DiagnosticsEngine.ReadLibraryFolder));
        Assert.Equal(first + ", " + second, finding.Where);
    }
}

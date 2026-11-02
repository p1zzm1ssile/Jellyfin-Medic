using System.Runtime.Versioning;

namespace MedicShared.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OthersCanRead = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _dir = Directory.CreateTempSubdirectory("medic-shared-tests-").FullName;

    private string FilePath => Path.Combine(_dir, "file.json");

    [Fact]
    public void Write_replaces_the_file_and_leaves_no_temporary_file()
    {
        AtomicFile.Write(FilePath, "one");
        AtomicFile.Write(FilePath, "two");

        Assert.Equal("two", File.ReadAllText(FilePath));
        Assert.Equal(new[] { "file.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_temporary_file_left_by_a_crash_is_written_over()
    {
        File.WriteAllText(FilePath + ".tmp", "half a fi");

        AtomicFile.Write(FilePath, "whole", ownerOnly: true);

        Assert.Equal("whole", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void An_owner_only_file_is_new_and_private_even_over_files_others_could_read()
    {
        // Permissions only apply to a file being created, so neither old file may be reused.
        File.WriteAllText(FilePath, "old");
        File.SetUnixFileMode(FilePath, OthersCanRead);
        File.WriteAllText(FilePath + ".tmp", "leftover");
        File.SetUnixFileMode(FilePath + ".tmp", OthersCanRead);

        AtomicFile.Write(FilePath, "new", ownerOnly: true);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void SetAside_keeps_each_damaged_file_under_its_own_name()
    {
        File.WriteAllText(FilePath, "first");
        string first = AtomicFile.SetAside(FilePath);
        File.WriteAllText(FilePath, "second");
        string second = AtomicFile.SetAside(FilePath);

        Assert.False(File.Exists(FilePath));
        Assert.Equal(("first", "second"), (File.ReadAllText(first), File.ReadAllText(second)));
        Assert.All(new[] { first, second }, kept => Assert.Matches(@"[/\\]file\.json\.\d{8}-\d{6}(-\d+)?\.bad$", kept));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

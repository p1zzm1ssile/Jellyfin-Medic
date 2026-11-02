using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicShared.Tests;

public sealed class PrivateJsonFileTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dir = Directory.CreateTempSubdirectory("medic-shared-tests-").FullName;

    private string KeysDir => Path.Combine(_dir, "plugin");

    private string FilePath => Path.Combine(KeysDir, "secrets.json");

    [Fact]
    public void Nothing_is_read_until_the_first_update()
    {
        var file = Open();
        Assert.Null(file.Read().First);

        file.Update(keys => keys.First = "one");

        Assert.Equal("one", Open().Read().First);
    }

    [Fact]
    public void Updates_made_at_the_same_time_are_all_kept()
    {
        var file = Open();
        for (int round = 0; round < 50; round++)
        {
            file.Update(keys => (keys.First, keys.Second) = (null, null));

            Parallel.Invoke(() => file.Update(keys => keys.First = "one"), () => file.Update(keys => keys.Second = "two"));

            var saved = file.Read();
            Assert.Equal(("one", "two"), (saved.First, saved.Second));
        }
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void The_file_is_created_readable_only_by_its_owner()
    {
        Open().Update(keys => keys.First = "one");

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(FilePath));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void A_file_others_can_read_is_made_private_when_first_read()
    {
        Directory.CreateDirectory(KeysDir);
        File.WriteAllText(FilePath, """{"first":"one"}""");
        File.SetUnixFileMode(FilePath, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal("one", Open().Read().First);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void A_damaged_file_reads_as_empty_and_is_left_alone()
    {
        WriteDamaged("{ not json");

        Assert.Null(Open().Read().First);
        Assert.Equal(new[] { "secrets.json" }, Directory.GetFiles(KeysDir).Select(Path.GetFileName));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Each_damaged_file_is_kept_aside_when_a_change_is_saved_over_it()
    {
        var file = Open();
        WriteDamaged("{ first damage");
        file.Update(keys => keys.First = "one");
        WriteDamaged("{ second damage");
        file.Update(keys => keys.Second = "two");

        var kept = Directory.GetFiles(KeysDir, "secrets.json.*.bad").Select(File.ReadAllText).Order();
        Assert.Equal(new[] { "{ first damage", "{ second damage" }, kept);
        var saved = file.Read();
        Assert.Equal(((string?)null, "two"), (saved.First, saved.Second));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PrivateJsonFile<Keys> Open() => new(FilePath, Options, NullLogger.Instance, "Medic tests");

    private void WriteDamaged(string text)
    {
        Directory.CreateDirectory(KeysDir);
        File.WriteAllText(FilePath, text);
    }

    private sealed class Keys
    {
        public string? First { get; set; }

        public string? Second { get; set; }
    }
}

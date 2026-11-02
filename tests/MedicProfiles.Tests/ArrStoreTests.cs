using Jellyfin.Plugin.MedicProfiles.Arr;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

public sealed class ArrStoreTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;

    public ArrStoreTests()
    {
        _store = new ArrStore(new TestPaths(_root), NullLogger<ArrStore>.Instance);
    }

    private string DataDir => Path.Combine(_root, "data", "medic-profiles");

    private string KeysFile => Path.Combine(DataDir, "secrets.json");

    // ---------- The keys file ----------

    [Fact]
    public void Each_key_and_the_password_are_kept_apart()
    {
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
        _store.SetKey(ArrApp.Radarr, "radarr-key");
        _store.SetQbitPassword("qbit-password");

        _store.SetKey(ArrApp.Sonarr, null);

        Assert.Null(_store.GetKey(ArrApp.Sonarr));
        Assert.Equal("radarr-key", _store.GetKey(ArrApp.Radarr));
        Assert.Equal("qbit-password", _store.GetQbitPassword());
    }

    [Fact]
    public void Keys_are_trimmed_but_the_password_is_kept_as_typed()
    {
        _store.SetKey(ArrApp.Radarr, "  radarr-key ");
        _store.SetQbitPassword(" pass word ");

        Assert.Equal("radarr-key", _store.GetKey(ArrApp.Radarr));
        Assert.Equal(" pass word ", _store.GetQbitPassword());
    }

    [Fact]
    public void Changes_made_at_the_same_time_are_all_kept()
    {
        for (int round = 0; round < 50; round++)
        {
            _store.SetKey(ArrApp.Sonarr, null);
            _store.SetKey(ArrApp.Radarr, null);
            _store.SetQbitPassword(null);

            Parallel.Invoke(
                () => _store.SetKey(ArrApp.Sonarr, "sonarr-key"),
                () => _store.SetKey(ArrApp.Radarr, "radarr-key"),
                () => _store.SetQbitPassword("qbit-password"));

            Assert.Equal(
                ("sonarr-key", "radarr-key", "qbit-password"),
                (_store.GetKey(ArrApp.Sonarr), _store.GetKey(ArrApp.Radarr), _store.GetQbitPassword()));
        }
    }

    [Fact]
    public void Saving_a_key_leaves_no_temporary_file_behind()
    {
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");

        Assert.Equal(new[] { "secrets.json" }, Directory.GetFiles(DataDir).Select(Path.GetFileName));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void The_keys_file_is_created_readable_only_by_its_owner()
    {
        _store.SetQbitPassword("qbit-password");

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(KeysFile));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void A_keys_file_others_can_read_is_made_private_when_first_read()
    {
        // As versions 1.1.1 and earlier saved it.
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(KeysFile, """{"sonarrKey":"sonarr-key"}""");
        File.SetUnixFileMode(KeysFile, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal("sonarr-key", _store.GetKey(ArrApp.Sonarr));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(KeysFile));
    }

    [Fact]
    public void A_damaged_keys_file_reads_as_no_keys_and_is_left_alone()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(KeysFile, "{ not json");

        Assert.Null(_store.GetKey(ArrApp.Radarr));
        Assert.Equal("{ not json", File.ReadAllText(KeysFile));
        Assert.Empty(Directory.GetFiles(DataDir, "*.bad"));
    }

    [Fact]
    public void Saving_over_a_damaged_keys_file_keeps_it_aside_with_the_time_in_its_name()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(KeysFile, "{ not json");

        _store.SetKey(ArrApp.Radarr, "radarr-key");

        string kept = Assert.Single(Directory.GetFiles(DataDir, "secrets.json.*.bad"));
        Assert.Equal("{ not json", File.ReadAllText(kept));
        Assert.Equal("radarr-key", _store.GetKey(ArrApp.Radarr));
    }

    // ---------- The action history ----------

    [Fact]
    public void The_history_keeps_the_newest_200_actions_newest_first()
    {
        for (int i = 1; i <= 205; i++)
        {
            _store.Record(new ActionRecord { Title = $"Title {i}" });
        }

        var history = _store.LoadHistory();

        Assert.Equal(200, history.Count);
        Assert.Equal(("Title 205", "Title 6"), (history[0].Title, history[^1].Title));
    }

    [Fact]
    public void A_damaged_history_reads_as_empty()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "history.json"), "[ not json");

        Assert.Empty(_store.LoadHistory());
    }

    [Fact]
    public void A_damaged_history_is_kept_aside_when_an_action_is_recorded()
    {
        // Writing over it would lose every action in it.
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "history.json"), "[ not json");

        _store.Record(new ActionRecord { Title = "Newest" });

        string kept = Assert.Single(Directory.GetFiles(DataDir, "history.json.*.bad"));
        Assert.Equal("[ not json", File.ReadAllText(kept));
        Assert.Equal(new[] { "Newest" }, _store.LoadHistory().Select(a => a.Title));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

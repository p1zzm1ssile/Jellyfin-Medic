using Jellyfin.Plugin.MedicPicks.Picks;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

public sealed class PicksStoreTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly PicksStore _store;

    public PicksStoreTests()
    {
        _store = new PicksStore(new TestPaths(_root), NullLogger<PicksStore>.Instance);
    }

    private string KeysDir => Path.Combine(_root, "data", "medic-picks");

    private string KeysFile => Path.Combine(KeysDir, "secrets.json");

    [Fact]
    public void A_title_hidden_while_picks_were_being_built_is_not_saved_with_them()
    {
        var user = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var built = new UserPicks
        {
            InLibrary = { new LibraryPick { ItemId = hidden }, new LibraryPick { ItemId = kept } },
            Discover = { new DiscoverPick { TmdbId = 603, MediaType = "movie" }, new DiscoverPick { TmdbId = 1399, MediaType = "tv" } }
        };

        // Hidden after the rebuild read the choices, before it saves.
        _store.UpdatePreferences(user, p =>
        {
            p.HiddenItems.Add(hidden);
            p.HiddenTmdb.Add(PicksStore.TmdbKey("tv", 1399));
            return p;
        });
        _store.SaveWithoutHidden(user, built);

        var saved = _store.Load(user)!;
        Assert.Equal(new[] { kept }, saved.InLibrary.Select(p => p.ItemId));
        Assert.Equal(new[] { 603 }, saved.Discover.Select(p => p.TmdbId));
    }

    [Fact]
    public void Changes_to_the_choices_made_at_the_same_time_are_all_kept()
    {
        var user = Guid.NewGuid();
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        Parallel.ForEach(ids, id => _store.UpdatePreferences(user, p =>
        {
            p.HiddenItems.Add(id);
            return p;
        }));

        Assert.Equal(ids.OrderBy(i => i), _store.LoadPreferences(user).HiddenItems.OrderBy(i => i));
    }

    // ---------- The keys file ----------

    [Fact]
    public void Keys_are_kept_trimmed_and_a_blank_one_removes_only_that_key()
    {
        _store.SetTmdbKey("  tmdb-key ");
        _store.SetSeerrKey("seerr-key");

        _store.SetSeerrKey("   ");

        Assert.Equal("tmdb-key", _store.GetTmdbKey());
        Assert.Null(_store.GetSeerrKey());
    }

    [Fact]
    public void Keys_saved_at_the_same_time_are_both_kept()
    {
        for (int round = 0; round < 50; round++)
        {
            _store.SetTmdbKey(null);
            _store.SetSeerrKey(null);

            Parallel.Invoke(() => _store.SetTmdbKey("tmdb-key"), () => _store.SetSeerrKey("seerr-key"));

            Assert.Equal(("tmdb-key", "seerr-key"), (_store.GetTmdbKey(), _store.GetSeerrKey()));
        }
    }

    [Fact]
    public void Saving_a_key_leaves_no_temporary_file_behind()
    {
        _store.SetTmdbKey("tmdb-key");

        Assert.Equal(new[] { "secrets.json" }, Directory.GetFiles(KeysDir).Select(Path.GetFileName));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void The_keys_file_is_created_readable_only_by_its_owner()
    {
        _store.SetTmdbKey("tmdb-key");

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(KeysFile));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void A_keys_file_others_can_read_is_made_private_when_first_read()
    {
        // As versions before 1.2.4 saved it.
        Directory.CreateDirectory(KeysDir);
        File.WriteAllText(KeysFile, """{"tmdbKey":"tmdb-key"}""");
        File.SetUnixFileMode(KeysFile, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal("tmdb-key", _store.GetTmdbKey());
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(KeysFile));
    }

    [Fact]
    public void A_damaged_keys_file_reads_as_no_keys_and_is_left_alone()
    {
        Directory.CreateDirectory(KeysDir);
        File.WriteAllText(KeysFile, "{ not json");

        Assert.Null(_store.GetTmdbKey());
        Assert.Equal("{ not json", File.ReadAllText(KeysFile));
        Assert.Empty(Directory.GetFiles(KeysDir, "*.bad"));
    }

    [Fact]
    public void Saving_over_a_damaged_keys_file_keeps_it_aside_with_the_time_in_its_name()
    {
        Directory.CreateDirectory(KeysDir);
        File.WriteAllText(KeysFile, "{ not json");

        _store.SetSeerrKey("seerr-key");

        string kept = Assert.Single(Directory.GetFiles(KeysDir, "secrets.json.*.bad"));
        Assert.Equal("{ not json", File.ReadAllText(kept));
        Assert.Equal("seerr-key", _store.GetSeerrKey());
    }

    // ---------- Each person's choices ----------

    [Fact]
    public void A_damaged_choices_file_is_kept_aside_when_a_change_is_saved_over_it()
    {
        // Writing over it would lose the titles hidden in it.
        var user = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        string prefsDir = Path.Combine(KeysDir, "preferences");
        Directory.CreateDirectory(prefsDir);
        File.WriteAllText(Path.Combine(prefsDir, user.ToString("N") + ".json"), "{ not json");

        _store.UpdatePreferences(user, p =>
        {
            p.HiddenItems.Add(hidden);
            return p;
        });

        string kept = Assert.Single(Directory.GetFiles(prefsDir, user.ToString("N") + ".json.*.bad"));
        Assert.Equal("{ not json", File.ReadAllText(kept));
        Assert.Equal(new[] { hidden }, _store.LoadPreferences(user).HiddenItems);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

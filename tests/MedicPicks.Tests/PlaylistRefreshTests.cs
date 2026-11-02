using System.Collections.Concurrent;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MedicPicks;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

/// <summary>The playlist refresh that follows "Not interested", run on the real engine and store.</summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class PlaylistRefreshTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _oldPlaylist = Guid.NewGuid();
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();
    private readonly Guid _third = Guid.NewGuid();
    private readonly PicksStore _store;
    private readonly FakeLibrary _library;
    private readonly FakePlaylists _playlists;
    private readonly IUserManager _users;

    public PlaylistRefreshTests()
    {
        var paths = new TestPaths(_root);
        _store = new PicksStore(paths, NullLogger<PicksStore>.Instance);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.UpdateConfiguration(new PluginConfiguration { EnableLibraryPicks = true, CreatePlaylists = true, MinimumWatchedTitles = 3 });

        var library = DispatchProxy.Create<ILibraryManager, FakeLibrary>();
        _library = (FakeLibrary)(object)library;
        _library.Items[_oldPlaylist] = new Playlist { Id = _oldPlaylist, OwnerUserId = _userId };

        var playlists = DispatchProxy.Create<IPlaylistManager, FakePlaylists>();
        _playlists = (FakePlaylists)(object)playlists;

        _users = DispatchProxy.Create<IUserManager, FakeUsers>();
        ((FakeUsers)(object)_users).User = new User("viewer", "a", "b") { Id = _userId };

        _store.Save(_userId, new UserPicks
        {
            GeneratedUtc = DateTime.UtcNow,
            PlaylistId = _oldPlaylist,
            InLibrary =
            {
                new LibraryPick { ItemId = _first, Kind = "Movie", Name = "Heat" },
                new LibraryPick { ItemId = _second, Kind = "Movie", Name = "Ronin" },
                new LibraryPick { ItemId = _third, Kind = "Movie", Name = "Thief" }
            }
        });
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task A_refresh_waiting_behind_a_build_puts_in_that_builds_picks_and_makes_no_second_playlist()
    {
        var engine = Engine(TimeSpan.Zero);

        // The build gets as far as removing the old playlist (too few titles watched now), and is held there.
        var held = new ManualResetEventSlim();
        var go = new ManualResetEventSlim();
        _library.OnDelete = () =>
        {
            held.Set();
            go.Wait();
        };
        var build = Task.Run(() => engine.BuildForUserAsync(_userId, new PicksRunContext(), CancellationToken.None));
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

        Hide(_first);
        var refresh = engine.RefreshPlaylistAsync(_userId);
        go.Set();
        await build;
        await refresh;

        var saved = _store.Load(_userId)!;
        Assert.Empty(_playlists.Created);
        Assert.Empty(_playlists.Updated);
        Assert.Null(saved.PlaylistId);
        Assert.Empty(saved.InLibrary);
        Assert.NotNull(saved.Note);
    }

    [Fact]
    public async Task Two_quick_hides_take_both_titles_out_in_one_playlist_update()
    {
        var engine = Engine(TimeSpan.FromMilliseconds(500));

        Hide(_first);
        var firstRefresh = Task.Run(() => engine.RefreshPlaylistAsync(_userId));
        Hide(_second);
        var secondRefresh = Task.Run(() => engine.RefreshPlaylistAsync(_userId));
        await Task.WhenAll(firstRefresh, secondRefresh);

        var update = Assert.Single(_playlists.Updated);
        Assert.Equal(_oldPlaylist, update.Id);
        Assert.Equal(new[] { _third }, update.Ids);
        Assert.Empty(_playlists.Created);
    }

    [Fact]
    public async Task A_hide_after_the_last_refresh_has_run_gets_a_refresh_of_its_own()
    {
        var engine = Engine(TimeSpan.Zero);

        Hide(_first);
        await engine.RefreshPlaylistAsync(_userId);
        Hide(_second);
        await engine.RefreshPlaylistAsync(_userId);

        Assert.Equal(2, _playlists.Updated.Count);
        Assert.Equal(new[] { _third }, _playlists.Updated.Last().Ids);
    }

    private PicksEngine Engine(TimeSpan delay) => new(
        (ILibraryManager)(object)_library,
        _users,
        null!,
        (IPlaylistManager)(object)_playlists,
        DispatchProxy.Create<ILocalizationManager, PicksControllerTests.NoLanguages>(),
        null!,
        _store,
        NullLogger<PicksEngine>.Instance)
    {
        PlaylistRefreshDelay = delay
    };

    // What the Hide endpoint does before it asks for the refresh.
    private void Hide(Guid itemId)
    {
        _store.UpdatePreferences(_userId, prefs =>
        {
            prefs.HiddenItems.Add(itemId);
            return prefs;
        });
        _store.RemoveHidden(_userId);
    }

    // Nothing watched and no titles; it holds the playlist the picks point at, and drops it when deleted.
    public class FakeLibrary : DispatchProxy
    {
        public ConcurrentDictionary<Guid, BaseItem> Items { get; } = new();

        public Action? OnDelete { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "GetItemList":
                    return new List<BaseItem>();
                case "GetItemById" when args!.Length == 1 && args[0] is Guid id:
                    return Items.TryGetValue(id, out var item) ? item : null;
                case "DeleteItem":
                    Items.TryRemove(((BaseItem)args![0]!).Id, out _);
                    OnDelete?.Invoke();
                    return null;
                case var name when name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal):
                    // The engine listens for library changes; these tests never raise one.
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    public class FakePlaylists : DispatchProxy
    {
        public ConcurrentQueue<PlaylistCreationRequest> Created { get; } = new();

        public List<PlaylistUpdateRequest> Updated { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "CreatePlaylist":
                    Created.Enqueue((PlaylistCreationRequest)args![0]!);
                    return Task.FromResult(new PlaylistCreationResult(Guid.NewGuid().ToString("N")));
                case "UpdatePlaylist":
                    lock (Updated)
                    {
                        Updated.Add((PlaylistUpdateRequest)args![0]!);
                    }

                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    public class FakeUsers : DispatchProxy
    {
        public User? User { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "GetUserById" ? User : throw new NotSupportedException(targetMethod.Name);
    }
}

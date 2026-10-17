using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MedicPicks;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

/// <summary>
/// A whole build against a small library: what it picks, and how often and how much it reads the library to do so.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class LibraryReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-reads-").FullName;
    private readonly User _user = new("viewer", "Default", "Default");
    private readonly Plugin _plugin;
    private readonly PicksStore _store;
    private readonly Library _library;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryManager? _itemsLibraryBefore = BaseItem.LibraryManager;
    private readonly PicksEngine _engine;

    public LibraryReadTests()
    {
        var paths = new TestPaths(_root);
        _plugin = new Plugin(paths, new DefaultSettingsSerializer());
        _store = new PicksStore(paths, NullLogger<PicksStore>.Instance);
        _library = Library.Make();
        _libraryManager = Proxy<ILibraryManager>(_library.Answer);

        // A box set finds its members through the library every item shares.
        BaseItem.LibraryManager = _libraryManager;

        var users = Proxy<IUserManager>((method, args) => method.Name == "GetUserById" && (Guid)args[0]! == _user.Id ? _user : null);
        var userData = Proxy<IUserDataManager>((method, args) => method.Name == "GetUserData"
            ? new UserItemData { Key = "k", LastPlayedDate = _library.LastPlayed(((BaseItem)args[1]!).Id) }
            : null);
        var playlists = Proxy<IPlaylistManager>((_, _) => throw new InvalidOperationException("playlists are off in these tests"));
        var tmdb = new TmdbClient(new OneClient(new HttpClient(new FakeTmdb())), NullLogger<TmdbClient>.Instance);

        _engine = new PicksEngine(_libraryManager, users, userData, playlists, Proxy<ILocalizationManager>((_, _) => null), tmdb, _store, NullLogger<PicksEngine>.Instance);
    }

    [Fact]
    public async Task A_build_picks_the_same_titles_from_people_genres_names_collections_and_franchises()
    {
        Configure(discover: false);

        var picks = await Build();

        Assert.Equal(
            """
            genres: Action, Animation, Comedy, Crime, Drama, Family, Fantasy, Sci-Fi, Thriller
            library: The Insider (Movie) Directed by Michael Mann, whose work you've watched
            library: Collateral (Movie) Directed by Michael Mann, whose work you've watched
            library: The Seven Deadly Sins: Prisoners of the Sky (Movie) You watch a lot of Action
            linked: The Seven Deadly Sins: Grudge of Edinburgh (Movie) From The Seven Deadly Sins
            linked: Thief (Movie) Part of Michael Mann Collection
            linked: Thor (Movie) Also in the Marvel Cinematic Universe (MCU)
            """,
            Describe(picks));
    }

    [Fact]
    public async Task Series_only_with_the_Anime_choice_picks_the_anime_series()
    {
        Configure(discover: false);
        _store.UpdatePreferences(_user.Id, p =>
        {
            p.Kind = "series";
            p.Genres = new List<string> { "Anime" };
            return p;
        });

        var picks = await Build();

        Assert.Equal(
            """
            genres: Action, Animation, Comedy, Crime, Drama, Family, Fantasy, Sci-Fi, Thriller
            library: Frieren: Beyond Journey's End (Series) Well rated, and close to your taste
            """,
            Describe(picks));
    }

    [Fact]
    public async Task Discover_leaves_out_what_is_already_on_the_server()
    {
        Configure(discover: true);

        var picks = await Build();

        Assert.Equal(
            new[] { "Mindhunter because of Severance", "Spider-Man: Far From Home because of Iron Man" },
            picks.Discover.Select(d => d.Title + " because of " + d.BecauseOf));
    }

    [Fact]
    public async Task A_build_reads_each_kind_of_title_once()
    {
        Configure(discover: true);

        await Build();

        // The person's films and series (genres and series to pick from), what they watched, the films they haven't,
        // the series they've started and the box sets: every read for this person, and no title read twice.
        var reads = _library.Reads.Where(r => r.ForUser).ToList();
        Assert.Equal(5, reads.Count);
        Assert.Single(reads, r => r.Types.Contains(BaseItemKind.Series));
    }

    [Fact]
    public async Task Each_read_asks_only_for_what_the_build_uses()
    {
        Configure(discover: true);

        await Build();

        Assert.All(_library.Reads, r =>
        {
            Assert.False(r.Images, "images: " + r);
            Assert.False(r.Settings, "locked fields: " + r);

            // Only the watched read needs when each title was last played.
            Assert.Equal(r.IsPlayed == true, r.UserData);

            // TMDb ids for Discover, and anime providers for the genre choices, on every read of films and series.
            bool titles = r.Types.Contains(BaseItemKind.Movie) || r.Types.Contains(BaseItemKind.Series);
            Assert.Equal(titles, r.ProviderIds);
        });
    }

    [Fact]
    public void Rebuilds_reuse_the_TMDb_ids_until_a_film_or_series_changes()
    {
        Configure(discover: true);

        var first = _engine.CreateRebuildContext();
        var second = _engine.CreateRebuildContext();
        Assert.Equal(1, ServerReads());
        Assert.Equal(first.LibraryTmdbIds.Order(), second.LibraryTmdbIds.Order());
        Assert.NotSame(first.LibraryTmdbIds, second.LibraryTmdbIds);

        // An episode doesn't carry the TMDb ids Discover leaves out; a film does.
        _library.Raise("ItemUpdated", new Episode { Id = Guid.NewGuid() });
        _engine.CreateRebuildContext();
        Assert.Equal(1, ServerReads());

        _library.Raise("ItemAdded", new Movie { Id = Guid.NewGuid() });
        _engine.CreateRebuildContext();
        Assert.Equal(2, ServerReads());

        // The nightly run always reads afresh.
        _engine.CreateRunContext();
        _engine.CreateRunContext();
        Assert.Equal(4, ServerReads());
        _engine.CreateRebuildContext();
        Assert.Equal(4, ServerReads());
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _itemsLibraryBefore;
        Directory.Delete(_root, recursive: true);
    }

    private int ServerReads() => _library.Reads.Count(r => !r.ForUser);

    private void Configure(bool discover)
    {
        _plugin.UpdateConfiguration(new PluginConfiguration { EnableDiscover = discover, CreatePlaylists = false, LibraryPickCount = 3 });
        _store.SetTmdbKey(discover ? "0123456789abcdef0123456789abcdef" : null);
    }

    private async Task<UserPicks> Build()
    {
        await _engine.BuildForUserAsync(_user.Id, _engine.CreateRunContext(), CancellationToken.None);
        return _store.Load(_user.Id)!;
    }

    private static string Describe(UserPicks picks)
    {
        var text = new StringBuilder("genres: " + string.Join(", ", picks.AvailableGenres));
        foreach (var p in picks.InLibrary)
        {
            text.Append("\nlibrary: ").Append(p.Name).Append(" (").Append(p.Kind).Append(") ").Append(p.Reason);
        }

        foreach (var p in picks.Linked)
        {
            text.Append("\nlinked: ").Append(p.Name).Append(" (").Append(p.Kind).Append(") ").Append(p.Reason);
        }

        return text.ToString();
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> answer)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Answers>();
        ((Answers)(object)proxy).Answer = answer;
        return proxy;
    }

    // Answers each call on an interface with the given function.
    public class Answers : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Answer { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Answer(targetMethod!, args ?? Array.Empty<object?>());
    }

    /// <summary>One library read, as the build asked for it.</summary>
    private sealed record Read(BaseItemKind[] Types, bool ForUser, bool? IsPlayed, bool Images, bool UserData, bool Settings, bool ProviderIds)
    {
        public override string ToString() => string.Join("+", Types) + (ForUser ? " for the person" : " for the server") + (IsPlayed is { } p ? " played=" + p : string.Empty);
    }

    /// <summary>
    /// A small library and one person's history in it: films and series watched, started and not, with their people,
    /// a box set and a franchise tag. Answers queries as Jellyfin does for the parts the build uses.
    /// </summary>
    private sealed class Library
    {
        private readonly List<BaseItem> _items = new();
        private readonly Dictionary<Guid, DateTime> _played = new();
        private readonly HashSet<Guid> _started = new();
        private readonly Dictionary<Guid, List<PersonInfo>> _people = new();
        private readonly Dictionary<string, List<Delegate>> _handlers = new(StringComparer.Ordinal);

        public List<Read> Reads { get; } = new();

        public static Library Make()
        {
            var library = new Library();
            var now = DateTime.UtcNow;

            var heat = library.Movie("Heat", 1995, 949, new[] { "Crime", "Thriller", "Drama" }, 8.3f);
            library.Watched(heat, now.AddDays(-10));
            library.Cast(heat, ("Michael Mann", PersonKind.Director), ("Al Pacino", PersonKind.Actor), ("Robert De Niro", PersonKind.Actor));

            var ironMan = library.Movie("Iron Man", 2008, 1726, new[] { "Action", "Sci-Fi" }, 7.6f, "marvel cinematic universe (mcu)");
            library.Watched(ironMan, now.AddDays(-30));
            library.Cast(ironMan, ("Jon Favreau", PersonKind.Director), ("Robert Downey Jr.", PersonKind.Actor), ("Chris Hemsworth", PersonKind.Actor));

            var sins = library.Series("The Seven Deadly Sins", 2014, 62104, new[] { "Action", "Animation" }, 7.8f);
            library.Watched(library.Episode(sins, 1, 1), now.AddDays(-5));
            library.Watched(library.Episode(sins, 1, 2), now.AddDays(-4));

            var severance = library.Series("Severance", 2022, 95396, new[] { "Drama", "sci-fi" }, 8.4f);
            library.Watched(library.Episode(severance, 1, 1), now.AddDays(-2));

            var collateral = library.Movie("Collateral", 2004, 1538, new[] { "Crime", "Thriller" }, 7.5f);
            library.Cast(collateral, ("Michael Mann", PersonKind.Director), ("Tom Cruise", PersonKind.Actor));
            var insider = library.Movie("The Insider", 1999, 9008, new[] { "Drama" }, 7.8f);
            library.Cast(insider, ("Michael Mann", PersonKind.Director), ("Al Pacino", PersonKind.Actor));
            var thief = library.Movie("Thief", 1981, 11524, new[] { "Crime" }, 7.3f);
            library.Cast(thief, ("Michael Mann", PersonKind.Director));
            var thor = library.Movie("Thor", 2011, 10195, new[] { "Fantasy" }, 6.8f, "marvel cinematic universe (mcu)");
            library.Cast(thor, ("Kenneth Branagh", PersonKind.Director), ("Chris Hemsworth", PersonKind.Actor));
            library.Movie("The Seven Deadly Sins: Prisoners of the Sky", 2018, 532067, new[] { "Action" }, 7.0f);
            library.Movie("Paddington", 2014, 116149, new[] { "Comedy", "Family" }, 7.3f);
            library.Movie("The Seven Deadly Sins: Grudge of Edinburgh", 2022, 1008042, new[] { "Fantasy" }, 6.1f);

            // Started, so neither is picked.
            var ronin = library.Movie("Ronin", 1998, 8195, new[] { "Action", "Thriller" }, 7.2f);
            library._started.Add(ronin.Id);
            var dark = library.Series("Dark", 2017, 70523, new[] { "Crime", "Drama" }, 8.4f);
            library._started.Add(library.Episode(dark, 1, 1).Id);

            var andor = library.Series("Andor", 2022, 83867, new[] { "Sci-Fi" }, 8.2f);
            library.Episode(andor, 1, 1);

            // Anime by its metadata provider alone.
            var frieren = library.Series("Frieren: Beyond Journey's End", 2023, 209867, new[] { "Fantasy" }, 8.8f);
            frieren.SetProviderId("AniList", "154587");
            library.Episode(frieren, 1, 1);

            library._items.Add(new BoxSet
            {
                Id = Guid.NewGuid(),
                Name = "Michael Mann Collection",
                LinkedChildren = new[] { heat, collateral, thief }.Select(m => new LinkedChild { ItemId = m.Id }).ToArray()
            });

            return library;
        }

        public DateTime? LastPlayed(Guid id) => _played.TryGetValue(id, out var when) ? when : null;

        public void Raise(string name, BaseItem item)
        {
            foreach (var handler in _handlers.GetValueOrDefault(name) ?? new())
            {
                ((EventHandler<ItemChangeEventArgs>)handler)(this, new ItemChangeEventArgs { Item = item });
            }
        }

        public object? Answer(MethodInfo method, object?[] args)
        {
            if (method.Name.StartsWith("add_", StringComparison.Ordinal))
            {
                string name = method.Name[4..];
                if (!_handlers.TryGetValue(name, out var list))
                {
                    list = new List<Delegate>();
                    _handlers[name] = list;
                }

                list.Add((Delegate)args[0]!);
                return null;
            }

            return method.Name switch
            {
                "GetItemList" when args.Length == 1 => Query((InternalItemsQuery)args[0]!),
                "GetItemById" when !method.IsGenericMethod => _items.FirstOrDefault(i => i.Id == (Guid)args[0]!),
                "GetPeople" when args[0] is BaseItem item => _people.GetValueOrDefault(item.Id) ?? new List<PersonInfo>(),
                _ => throw new NotSupportedException(method.ToString())
            };
        }

        private List<BaseItem> Query(InternalItemsQuery q)
        {
            if (q.ItemIds.Length > 0)
            {
                return _items.Where(i => q.ItemIds.Contains(i.Id)).ToList();
            }

            Reads.Add(new Read(
                q.IncludeItemTypes,
                q.User is not null,
                q.IsPlayed,
                q.DtoOptions.EnableImages,
                q.DtoOptions.EnableUserData,
                q.DtoOptions.ContainsField(ItemFields.Settings),
                q.DtoOptions.ContainsField(ItemFields.ProviderIds)));

            // Jellyfin returns titles in sort-name order when no order is asked for.
            return _items
                .Where(i => q.IncludeItemTypes.Contains(i.GetBaseItemKind()))
                .Where(i => q.IsPlayed is not { } played || played == _played.ContainsKey(i.Id))
                .Where(i => q.IsResumable is not { } resumable || resumable == _started.Contains(i.Id))
                .Where(i => q.AncestorIds.Length == 0 || (i is Episode e && q.AncestorIds.Contains(e.SeriesId)))
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .ToList();
        }

        private Movie Movie(string name, int year, int tmdb, string[] genres, float rating, params string[] tags)
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = year, Genres = genres, CommunityRating = rating, Tags = tags };
            movie.SetProviderId(MetadataProvider.Tmdb, tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _items.Add(movie);
            return movie;
        }

        private Series Series(string name, int year, int tmdb, string[] genres, float rating)
        {
            var series = new Series { Id = Guid.NewGuid(), Name = name, ProductionYear = year, Genres = genres, CommunityRating = rating };
            series.SetProviderId(MetadataProvider.Tmdb, tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _items.Add(series);
            return series;
        }

        private Episode Episode(Series series, int season, int number)
        {
            var episode = new Episode { Id = Guid.NewGuid(), Name = $"{series.Name} {season}x{number}", SeriesId = series.Id, ParentIndexNumber = season, IndexNumber = number };
            _items.Add(episode);
            return episode;
        }

        private void Watched(BaseItem item, DateTime when) => _played[item.Id] = when;

        private void Cast(BaseItem item, params (string Name, PersonKind Kind)[] people) =>
            _people[item.Id] = people.Select(p => new PersonInfo { Name = p.Name, Type = p.Kind }).ToList();
    }

    /// <summary>TMDb's recommendations for the two titles Discover starts from here, one of them already on the server.</summary>
    private sealed class FakeTmdb : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = path switch
            {
                "/3/movie/1726/recommendations" => """{"results":[{"id":10195,"title":"Thor","vote_average":6.8},{"id":429617,"title":"Spider-Man: Far From Home","vote_average":7.4}]}""",
                "/3/tv/95396/recommendations" => """{"results":[{"id":67744,"name":"Mindhunter","vote_average":8.3},{"id":70523,"name":"Dark","vote_average":8.4}]}""",
                _ => """{"results":[]}"""
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class OneClient(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

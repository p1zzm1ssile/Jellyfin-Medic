using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Builds picks for one user from their watch history.
/// Library picks use genres and people from what they've watched; Discover picks use TMDb recommendations.
/// Everything runs through InternalItemsQuery(user), so library access and parental controls are respected.
/// </summary>
public class PicksEngine
{
    private const int MaxTitlesForPeople = 60;
    private const int ShortlistSize = 150;
    private const int DiscoverSeeds = 8;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly TmdbClient _tmdb;
    private readonly PicksStore _store;
    private readonly ILogger<PicksEngine> _logger;

    public PicksEngine(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPlaylistManager playlistManager,
        TmdbClient tmdb,
        PicksStore store,
        ILogger<PicksEngine> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _tmdb = tmdb;
        _store = store;
        _logger = logger;
    }

    /// <summary>Collect run-wide data once: the TMDb key and every TMDb id already on the server.</summary>
    public PicksRunContext CreateRunContext()
    {
        var context = new PicksRunContext { TmdbKey = _store.GetTmdbKey() };

        var everything = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var item in everything)
        {
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(tmdbId))
            {
                context.LibraryTmdbIds.Add((item is Series ? "tv:" : "movie:") + tmdbId);
            }
        }

        return context;
    }

    // One build per person at a time: the nightly task and a preferences save could otherwise both
    // replace the playlist, leaving a duplicate behind.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> UserLocks = new();

    public async Task BuildForUserAsync(Guid userId, PicksRunContext context, CancellationToken cancellationToken)
    {
        var gate = UserLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BuildForUserLockedAsync(userId, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task BuildForUserLockedAsync(Guid userId, PicksRunContext context, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        // A factory keeps the User type out of method signatures (its namespace moved between Jellyfin versions).
        Func<InternalItemsQuery> userQuery = () => new InternalItemsQuery(user);

        var previous = _store.Load(userId);
        var prefs = _store.LoadPreferences(userId);
        var language = AudioLanguage.FromTmdb(config.TmdbLanguage);
        var picks = new UserPicks { GeneratedUtc = DateTime.UtcNow, PlaylistId = previous?.PlaylistId };
        picks.AvailableGenres = LibraryGenres(userQuery);

        // 1. What has this user watched? Episodes roll up to their series.
        var watchedQuery = userQuery();
        watchedQuery.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode };
        watchedQuery.IsPlayed = true;
        watchedQuery.Recursive = true;
        watchedQuery.IsVirtualItem = false;
        var watchedItems = _libraryManager.GetItemList(watchedQuery);

        var titles = new Dictionary<Guid, WatchedTitle>();
        var seriesCache = new Dictionary<Guid, BaseItem?>();

        foreach (var item in watchedItems)
        {
            BaseItem? title = item;
            if (item is Episode episode)
            {
                if (!seriesCache.TryGetValue(episode.SeriesId, out title))
                {
                    title = _libraryManager.GetItemById(episode.SeriesId);
                    seriesCache[episode.SeriesId] = title;
                }
            }

            if (title is null)
            {
                continue;
            }

            if (!titles.TryGetValue(title.Id, out var watched))
            {
                watched = new WatchedTitle(title);
                titles[title.Id] = watched;
            }

            watched.Count++;
            var lastPlayed = _userDataManager.GetUserData(user, item)?.LastPlayedDate;
            if (lastPlayed.HasValue && (!watched.LastPlayed.HasValue || lastPlayed > watched.LastPlayed))
            {
                watched.LastPlayed = lastPlayed;
            }
        }

        if (titles.Count < Math.Max(1, config.MinimumWatchedTitles))
        {
            picks.Note = "Watch a few more things and your picks will appear here after the next nightly run.";
            await ReplacePlaylistAsync(userId, picks, Array.Empty<Guid>(), config.PlaylistName, false).ConfigureAwait(false);
            _store.Save(userId, picks);
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var t in titles.Values)
        {
            var baseWeight = t.Item is Series ? Math.Min(3.0, 1.0 + ((t.Count - 1) / 5.0)) : 1.0;
            var days = t.LastPlayed.HasValue ? Math.Max(0, (now - t.LastPlayed.Value).TotalDays) : 365;
            var recency = 1.0 / (1.0 + (days / 90.0));
            t.Weight = baseWeight * (0.4 + (0.6 * recency));
        }

        var ranked = titles.Values.OrderByDescending(t => t.Weight).ToList();

        // 2. Library picks.
        if (config.EnableLibraryPicks)
        {
            int libraryCount = prefs.Count > 0 ? prefs.Count : Math.Clamp(config.LibraryPickCount, 1, 100);
            picks.InLibrary = BuildLibraryPicks(userQuery, ranked, titles, libraryCount, prefs, language);
        }

        // 2b. Titles from the same world as something watched.
        if (config.EnableLinkedPicks)
        {
            int linkedCount = prefs.Count > 0 ? prefs.Count : Math.Clamp(config.LibraryPickCount, 1, 100);
            var alreadyPicked = new HashSet<Guid>(picks.InLibrary.Select(p => p.ItemId));
            try
            {
                picks.Linked = BuildLinkedPicks(userQuery, ranked, titles, linkedCount, prefs, language, alreadyPicked);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Medic Picks: couldn't build linked picks for user {UserId}", userId);
            }
        }

        // 3. Private playlist, visible in every Jellyfin app.
        var playlistItems = new List<Guid>();
        if (config.EnableLibraryPicks && config.CreatePlaylists)
        {
            foreach (var pick in picks.InLibrary)
            {
                var playable = GetPlayableId(userQuery, pick);
                if (playable.HasValue)
                {
                    playlistItems.Add(playable.Value);
                }
            }
        }

        await ReplacePlaylistAsync(userId, picks, playlistItems, config.PlaylistName, config.EnableLibraryPicks && config.CreatePlaylists)
            .ConfigureAwait(false);

        // Save the new playlist's ID straight away (keeping last run's Discover picks for now), so if the
        // TMDb step below fails or is stopped, the next run can still find this playlist and replace it.
        picks.Discover = previous?.Discover ?? picks.Discover;
        _store.Save(userId, picks);

        // 4. Discover picks from TMDb.
        var discoverAllowed = config.EnableDiscover
            && !string.IsNullOrEmpty(context.TmdbKey)
            && !config.DiscoverDisabledUserIds.Any(id => Guid.TryParse(id, out var g) && g == userId);

        picks.Discover = discoverAllowed
            ? await BuildDiscoverPicksAsync(ranked, context, config.TmdbLanguage, prefs.Count > 0 ? prefs.Count : Math.Clamp(config.DiscoverPickCount, 1, 100), prefs, cancellationToken)
                .ConfigureAwait(false)
            : new List<DiscoverPick>();

        _store.Save(userId, picks);
    }

    /// <summary>
    /// Titles from the same world as something this person watched: a series' films and the other way
    /// round (matched on the name, e.g. "The Seven Deadly Sins" and "The Seven Deadly Sins: Prisoners of the
    /// Sky"), the rest of a collection, and titles sharing a franchise tag such as "marvel cinematic universe
    /// (mcu)". Only titles they can see and haven't watched or started. Genre choices aren't applied here,
    /// because a franchise spans genres.
    /// </summary>
    private List<LibraryPick> BuildLinkedPicks(
        Func<InternalItemsQuery> userQuery,
        List<WatchedTitle> ranked,
        Dictionary<Guid, WatchedTitle> titles,
        int count,
        UserPreferences prefs,
        AudioLanguage language,
        HashSet<Guid> alreadyPicked)
    {
        var pool = new List<BaseItem>();
        if (prefs.Kind != "series")
        {
            var movies = userQuery();
            movies.IncludeItemTypes = new[] { BaseItemKind.Movie };
            movies.IsPlayed = false;
            movies.Recursive = true;
            movies.IsVirtualItem = false;
            pool.AddRange(_libraryManager.GetItemList(movies));
        }

        if (prefs.Kind != "movies")
        {
            var series = userQuery();
            series.IncludeItemTypes = new[] { BaseItemKind.Series };
            series.Recursive = true;
            series.IsVirtualItem = false;
            pool.AddRange(_libraryManager.GetItemList(series));
        }

        var hidden = new HashSet<Guid>(prefs.HiddenItems);
        var poolById = pool
            .Where(c => !titles.ContainsKey(c.Id) && !hidden.Contains(c.Id) && !alreadyPicked.Contains(c.Id))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First());
        if (poolById.Count == 0)
        {
            return new List<LibraryPick>();
        }

        var found = new Dictionary<Guid, LinkedTally>();
        void Link(BaseItem item, WatchedTitle seed, string reason, double strength)
        {
            if (!found.TryGetValue(item.Id, out var tally))
            {
                tally = new LinkedTally(item);
                found[item.Id] = tally;
            }

            double score = seed.Weight * strength;
            tally.Score += score;
            if (score > tally.BestScore)
            {
                tally.BestScore = score;
                tally.Reason = reason;
            }
        }

        // 1. Names: a series and its films, a film and its sequels.
        var poolNames = poolById.Values.Select(c => (Item: c, Key: NameKey(c.Name))).Where(x => x.Key.Length > 0).ToList();
        foreach (var seed in ranked)
        {
            string key = NameKey(seed.Item.Name);
            if (key.Length == 0)
            {
                continue;
            }

            foreach (var (item, itemKey) in poolNames)
            {
                bool sameKind = item.GetType() == seed.Item.GetType();
                string shorter = itemKey.Length < key.Length ? itemKey : key;
                bool prefix = itemKey.StartsWith(key + " ", StringComparison.Ordinal) || key.StartsWith(itemKey + " ", StringComparison.Ordinal);

                // Same name: a film and a series of it. One name starting with the other: a sequel or spin-off,
                // as long as the shorter name is distinctive. A one-word name ("Avatar") only links the same
                // kind, so the Avatar films don't pull in Avatar: The Last Airbender.
                bool linked = itemKey == key
                    ? !sameKind && Distinctive(key)
                    : prefix && Distinctive(shorter) && (shorter.Contains(' ', StringComparison.Ordinal) || sameKind);
                if (linked)
                {
                    Link(item, seed, "From " + seed.Item.Name, 1.0);
                }
            }
        }

        // 2. Collections: the rest of a collection something was watched from.
        var boxQuery = userQuery();
        boxQuery.IncludeItemTypes = new[] { BaseItemKind.BoxSet };
        boxQuery.Recursive = true;
        foreach (var box in _libraryManager.GetItemList(boxQuery).OfType<Folder>())
        {
            List<BaseItem> members;
            try
            {
                members = box.GetLinkedChildren().ToList();
            }
            catch
            {
                continue;
            }

            var seed = members.Where(m => titles.ContainsKey(m.Id)).Select(m => titles[m.Id]).OrderByDescending(w => w.Weight).FirstOrDefault();
            if (seed is null)
            {
                continue;
            }

            foreach (var member in members)
            {
                if (poolById.TryGetValue(member.Id, out var item))
                {
                    Link(item, seed, "Part of " + box.Name, 0.9);
                }
            }
        }

        // 3. Franchise tags shared with something watched.
        var tagSeeds = new Dictionary<string, WatchedTitle>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in ranked)
        {
            foreach (var tag in (seed.Item.Tags ?? Array.Empty<string>()).Where(IsFranchiseTag))
            {
                tagSeeds.TryAdd(tag, seed); // ranked, so the first is the most-weighted
            }
        }

        if (tagSeeds.Count > 0)
        {
            foreach (var item in poolById.Values)
            {
                foreach (var tag in item.Tags ?? Array.Empty<string>())
                {
                    if (tagSeeds.TryGetValue(tag, out var seed))
                    {
                        Link(item, seed, "Also in the " + FranchiseName(tag), 0.7);
                        break;
                    }
                }
            }
        }

        // Strongest links first; within the same link, in release order.
        return found.Values
            .OrderByDescending(t => Math.Round(t.Score, 3))
            .ThenBy(t => t.Item.ProductionYear ?? int.MaxValue)
            .Where(t => !prefs.DubbedOnly || HasAudioIn(userQuery, t.Item, language))
            .Take(count)
            .Select(t => new LibraryPick
            {
                ItemId = t.Item.Id,
                Name = t.Item.Name,
                Year = t.Item.ProductionYear,
                Kind = t.Item is Series ? "Series" : "Movie",
                Reason = t.Reason
            })
            .ToList();
    }

    /// <summary>A name to compare on: lower case, letters and digits only, without a leading "the".</summary>
    private static string NameKey(string? name)
    {
        var chars = (name ?? string.Empty).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        string key = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return key.StartsWith("the ", StringComparison.Ordinal) ? key[4..] : key;
    }

    // Short one-word names ("up", "lost", "it") would link unrelated titles, so they aren't used.
    private static bool Distinctive(string key) => key.Contains(' ', StringComparison.Ordinal) || key.Length >= 6;

    private static bool IsFranchiseTag(string tag)
    {
        string t = tag.ToLowerInvariant();
        return t.Contains("universe", StringComparison.Ordinal) || t.Contains("franchise", StringComparison.Ordinal)
            || t.Contains("(mcu)", StringComparison.Ordinal) || t.Contains("dceu", StringComparison.Ordinal)
            || t.Contains("monsterverse", StringComparison.Ordinal) || t.Contains("wizarding world", StringComparison.Ordinal);
    }

    /// <summary>"marvel cinematic universe (mcu)" becomes "Marvel Cinematic Universe (MCU)".</summary>
    private static string FranchiseName(string tag)
    {
        string name = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag.Trim().ToLowerInvariant());

        // Short words with no vowels are initials: "Dc" becomes "DC".
        name = string.Join(' ', name.Split(' ').Select(w => w.Length is >= 2 and <= 3 && !w.Any(c => "aeiouAEIOU".Contains(c)) && w.All(char.IsLetter) ? w.ToUpperInvariant() : w));
        int open = name.IndexOf('(', StringComparison.Ordinal);
        int close = name.IndexOf(')', StringComparison.Ordinal);
        if (open >= 0 && close > open && close - open <= 6)
        {
            name = name[..open] + name[open..(close + 1)].ToUpperInvariant() + name[(close + 1)..];
        }

        return name;
    }

    /// <summary>Every genre among the films and series this person can see, for the choices on their page.</summary>
    private List<string> LibraryGenres(Func<InternalItemsQuery> userQuery)
    {
        try
        {
            var query = userQuery();
            query.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series };
            query.Recursive = true;
            query.IsVirtualItem = false;
            return _libraryManager.GetItemList(query)
                .SelectMany(i => i.Genres ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: couldn't list genres");
            return new List<string>();
        }
    }

    private List<LibraryPick> BuildLibraryPicks(Func<InternalItemsQuery> userQuery, List<WatchedTitle> ranked, Dictionary<Guid, WatchedTitle> titles, int count, UserPreferences prefs, AudioLanguage language)
    {
        // Taste profile: genres from everything watched, people from the most-weighted titles.
        var genres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ranked)
        {
            foreach (var g in t.Item.Genres ?? Array.Empty<string>())
            {
                genres[g] = genres.GetValueOrDefault(g) + t.Weight;
            }
        }

        var people = new Dictionary<string, PersonTaste>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ranked.Take(MaxTitlesForPeople))
        {
            var actorsSeen = 0;
            foreach (var p in _libraryManager.GetPeople(t.Item))
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    continue;
                }

                double w;
                if (p.Type == PersonKind.Director)
                {
                    w = 2.0 * t.Weight;
                }
                else if (p.Type == PersonKind.Actor && actorsSeen < 5)
                {
                    actorsSeen++;
                    w = 1.0 * t.Weight;
                }
                else
                {
                    continue;
                }

                if (!people.TryGetValue(p.Name, out var taste))
                {
                    taste = new PersonTaste(p.Name, p.Type == PersonKind.Director);
                    people[p.Name] = taste;
                }

                taste.Weight += w;
            }
        }

        var maxGenre = genres.Count > 0 ? genres.Values.Max() : 1.0;
        var maxPerson = people.Count > 0 ? people.Values.Max(p => p.Weight) : 1.0;

        // Candidates: unwatched movies, and series the user hasn't started.
        var movieQuery = userQuery();
        movieQuery.IncludeItemTypes = new[] { BaseItemKind.Movie };
        movieQuery.IsPlayed = false;
        movieQuery.Recursive = true;
        movieQuery.IsVirtualItem = false;

        var seriesQuery = userQuery();
        seriesQuery.IncludeItemTypes = new[] { BaseItemKind.Series };
        seriesQuery.Recursive = true;
        seriesQuery.IsVirtualItem = false;

        var candidates = new List<BaseItem>();
        if (prefs.Kind != "series")
        {
            candidates.AddRange(_libraryManager.GetItemList(movieQuery));
        }

        if (prefs.Kind != "movies")
        {
            candidates.AddRange(_libraryManager.GetItemList(seriesQuery).Where(s => !titles.ContainsKey(s.Id)));
        }

        // The person's own choices: titles they hid, and the genres they picked.
        var hidden = new HashSet<Guid>(prefs.HiddenItems);
        candidates = candidates
            .Where(c => !hidden.Contains(c.Id))
            .Where(c => Genres.Matches(prefs.Genres, c.Genres, c.Tags, c.Name, prefs.Genres.Count > 0 && IsAnime(c)))
            .ToList();

        // First pass: genres + rating (cheap). Second pass: people (one lookup per shortlisted item).
        var scored = candidates
            .Select(c =>
            {
                var itemGenres = c.Genres ?? Array.Empty<string>();
                var genreScore = itemGenres.Sum(g => genres.GetValueOrDefault(g) / maxGenre) / Math.Sqrt(Math.Max(1, itemGenres.Length));
                var rating = (c.CommunityRating ?? 6.5f) / 10.0;
                return new Scored(c, genreScore + (0.1 * rating), genreScore);
            })
            .OrderByDescending(s => s.Score)
            .Take(ShortlistSize)
            .ToList();

        foreach (var s in scored)
        {
            foreach (var p in _libraryManager.GetPeople(s.Item))
            {
                if (string.IsNullOrWhiteSpace(p.Name) || !people.TryGetValue(p.Name, out var taste))
                {
                    continue;
                }

                var contribution = taste.Weight / maxPerson;
                s.PeopleScore += contribution;
                if (contribution > s.BestPersonScore)
                {
                    s.BestPersonScore = contribution;
                    s.BestPerson = taste;
                }
            }

            s.Score += 0.5 * Math.Min(2.0, s.PeopleScore);
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Where(s => !prefs.DubbedOnly || HasAudioIn(userQuery, s.Item, language))
            .Take(count)
            .Select(s => new LibraryPick
            {
                ItemId = s.Item.Id,
                Name = s.Item.Name,
                Year = s.Item.ProductionYear,
                Kind = s.Item is Series ? "Series" : "Movie",
                Reason = BuildReason(s, genres)
            })
            .ToList();
    }

    private static string BuildReason(Scored s, Dictionary<string, double> genres)
    {
        if (s.BestPerson is not null && s.BestPersonScore >= 0.3)
        {
            return s.BestPerson.IsDirector
                ? $"Directed by {s.BestPerson.Name}, whose work you've watched"
                : $"With {s.BestPerson.Name}, from things you've watched";
        }

        var top = (s.Item.Genres ?? Array.Empty<string>())
            .Where(genres.ContainsKey)
            .OrderByDescending(g => genres[g])
            .Take(2)
            .ToList();

        return top.Count switch
        {
            2 => $"You watch a lot of {top[0]} and {top[1]}",
            1 => $"You watch a lot of {top[0]}",
            _ => "Well rated, and close to your taste"
        };
    }

    // ---------- English dubs for anime ----------

    private static readonly string[] AnimeProviders = { "AniList", "AniDB", "Kitsu", "AniSearch", "MyAnimeList" };

    /// <summary>Anime, judged by genre, an anime metadata provider, or an "anime" folder in its path.</summary>
    private static bool IsAnime(BaseItem item)
    {
        if ((item.Genres ?? Array.Empty<string>()).Any(g => g.Equals("Anime", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (item.ProviderIds is { } ids && ids.Keys.Any(k => AnimeProviders.Any(p => k.Equals(p, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        string path = (item.Path ?? string.Empty).Replace('\\', '/');
        return path.Contains("/anime/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Has audio in the given language. A series is judged by its first episode. When no audio track
    /// says its language, the film is assumed to be in it, except anime, where untagged audio is
    /// usually Japanese.
    /// </summary>
    private bool HasAudioIn(Func<InternalItemsQuery> userQuery, BaseItem item, AudioLanguage wanted)
    {
        BaseItem? target = item;
        if (item is Series)
        {
            var first = GetPlayableId(userQuery, new LibraryPick { ItemId = item.Id, Kind = "Series" });
            target = first.HasValue ? _libraryManager.GetItemById(first.Value) : null;
        }

        if (target is null)
        {
            return false;
        }

        try
        {
            var method = target.GetType().GetMethod("GetMediaStreams", Type.EmptyTypes);
            if (method?.Invoke(target, null) is not System.Collections.IEnumerable streams)
            {
                return false;
            }

            bool anyTagged = false;
            foreach (var stream in streams)
            {
                var type = stream.GetType();
                string kind = type.GetProperty("Type")?.GetValue(stream)?.ToString() ?? string.Empty;
                if (kind != "Audio")
                {
                    continue;
                }

                string language = (type.GetProperty("Language")?.GetValue(stream) as string ?? string.Empty).Trim().ToLowerInvariant();
                if (wanted.Matches(language))
                {
                    return true;
                }

                anyTagged |= language.Length > 0 && language != "und";
            }

            return !anyTagged && !IsAnime(item);
        }
        catch
        {
            // Unknown counts as no English audio.
        }

        return false;
    }

    /// <summary>Movies play as themselves; a series is represented by its first regular episode.</summary>
    private Guid? GetPlayableId(Func<InternalItemsQuery> userQuery, LibraryPick pick)
    {
        if (pick.Kind != "Series")
        {
            return pick.ItemId;
        }

        var episodeQuery = userQuery();
        episodeQuery.IncludeItemTypes = new[] { BaseItemKind.Episode };
        episodeQuery.AncestorIds = new[] { pick.ItemId };
        episodeQuery.Recursive = true;
        episodeQuery.IsVirtualItem = false;
        var episodes = _libraryManager.GetItemList(episodeQuery);

        var first = episodes
            .Where(e => (e.ParentIndexNumber ?? 1) > 0)
            .OrderBy(e => e.ParentIndexNumber ?? 1)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .FirstOrDefault();

        return first?.Id;
    }

    /// <summary>Deletes last run's private playlist and creates a fresh one owned by the user.</summary>
    private async Task ReplacePlaylistAsync(Guid userId, UserPicks picks, IReadOnlyList<Guid> items, string name, bool create)
    {
        try
        {
            if (picks.PlaylistId.HasValue)
            {
                var old = _libraryManager.GetItemById(picks.PlaylistId.Value);
                if (old is Playlist playlist && playlist.OwnerUserId == userId)
                {
                    _libraryManager.DeleteItem(old, new DeleteOptions { DeleteFileLocation = true });
                }

                picks.PlaylistId = null;
            }

            if (!create || items.Count == 0)
            {
                return;
            }

            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Picks for you" : name.Trim(),
                ItemIdList = items.ToArray(),
                MediaType = Jellyfin.Data.Enums.MediaType.Video,
                UserId = userId,
                Public = false
            }).ConfigureAwait(false);

            if (Guid.TryParse(result.Id.ToString(), out var newId))
            {
                picks.PlaylistId = newId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not update the playlist for user {UserId}", userId);
        }
    }

    private async Task<List<DiscoverPick>> BuildDiscoverPicksAsync(
        List<WatchedTitle> ranked,
        PicksRunContext context,
        string language,
        int count,
        UserPreferences prefs,
        CancellationToken cancellationToken)
    {
        // Recommendations are the same kind as the title they come from, so "movies only" uses only films as seeds.
        var seeds = ranked
            .Select(t => new { t.Item, t.Weight, TmdbId = t.Item.GetProviderId(MetadataProvider.Tmdb) })
            .Where(s => !string.IsNullOrEmpty(s.TmdbId) && (s.Item is Movie || s.Item is Series))
            .Where(s => prefs.Kind == "all" || (prefs.Kind == "movies") == (s.Item is Movie))
            .Take(DiscoverSeeds)
            .ToList();

        var tally = new Dictionary<string, DiscoverTally>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(prefs.HiddenTmdb, StringComparer.Ordinal);

        foreach (var seed in seeds)
        {
            var mediaType = seed.Item is Series ? "tv" : "movie";
            var key = mediaType + ":" + seed.TmdbId;

            if (!context.TmdbCache.TryGetValue(key, out var recs))
            {
                recs = await _tmdb.GetRecommendationsAsync(mediaType, seed.TmdbId!, context.TmdbKey!, language, cancellationToken)
                    .ConfigureAwait(false);
                context.TmdbCache[key] = recs;
                await Task.Delay(60, cancellationToken).ConfigureAwait(false); // stay well under TMDb's rate limit
            }

            for (var i = 0; i < recs.Count; i++)
            {
                var rec = recs[i];
                var recKey = rec.MediaType + ":" + rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (context.LibraryTmdbIds.Contains(recKey) || hidden.Contains(recKey))
                {
                    continue; // already on the server, or this person isn't interested
                }

                bool recAnime = string.Equals(rec.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase) && rec.GenreIds.Contains(16);
                if (!Genres.Matches(prefs.Genres, rec.GenreIds.Select(Genres.TmdbName), null, rec.Title, recAnime))
                {
                    continue; // not one of the genres they chose
                }

                // Earlier results from a heavily watched seed count for more.
                var contribution = seed.Weight * (1.0 / (1.0 + (i / 5.0)));
                if (!tally.TryGetValue(recKey, out var entry))
                {
                    entry = new DiscoverTally(rec);
                    tally[recKey] = entry;
                }

                entry.Score += contribution;
                if (contribution > entry.BestContribution)
                {
                    entry.BestContribution = contribution;
                    entry.BecauseOf = seed.Item.Name;
                }
            }
        }

        return tally.Values
            .OrderByDescending(t => t.Score + (t.Title.VoteAverage / 100.0))
            .Take(count)
            .Select(t => new DiscoverPick
            {
                TmdbId = t.Title.Id,
                MediaType = t.Title.MediaType,
                Title = t.Title.Title,
                Year = t.Title.Year,
                Overview = Truncate(t.Title.Overview, 280),
                PosterPath = t.Title.PosterPath,
                Rating = Math.Round(t.Title.VoteAverage, 1),
                BecauseOf = t.BecauseOf,
                IsAnime = string.Equals(t.Title.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase) && t.Title.GenreIds.Contains(16),
                OriginalLanguage = t.Title.OriginalLanguage
            })
            .ToList();
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', max);
        return text[..(cut > 0 ? cut : max)] + "…";
    }

    private sealed class WatchedTitle
    {
        public WatchedTitle(BaseItem item) => Item = item;

        public BaseItem Item { get; }

        public int Count { get; set; }

        public DateTime? LastPlayed { get; set; }

        public double Weight { get; set; }
    }

    private sealed class PersonTaste
    {
        public PersonTaste(string name, bool isDirector)
        {
            Name = name;
            IsDirector = isDirector;
        }

        public string Name { get; }

        public bool IsDirector { get; }

        public double Weight { get; set; }
    }

    private sealed class Scored
    {
        public Scored(BaseItem item, double score, double genreScore)
        {
            Item = item;
            Score = score;
            GenreScore = genreScore;
        }

        public BaseItem Item { get; }

        public double Score { get; set; }

        public double GenreScore { get; }

        public double PeopleScore { get; set; }

        public double BestPersonScore { get; set; }

        public PersonTaste? BestPerson { get; set; }
    }

    private sealed class LinkedTally
    {
        public LinkedTally(BaseItem item) => Item = item;

        public BaseItem Item { get; }

        public double Score { get; set; }

        public double BestScore { get; set; }

        public string Reason { get; set; } = string.Empty;
    }

    private sealed class DiscoverTally
    {
        public DiscoverTally(TmdbTitle title) => Title = title;

        public TmdbTitle Title { get; }

        public double Score { get; set; }

        public double BestContribution { get; set; }

        public string BecauseOf { get; set; } = string.Empty;
    }
}

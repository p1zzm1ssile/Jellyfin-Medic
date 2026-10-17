using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Querying;
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

    // Reading the TMDb ids on the server means reading every film and series, so rebuilds from the picks page reuse
    // them for a while. A film or series added, changed or removed clears them, and the nightly run reads them afresh.
    private static readonly TimeSpan LibraryIdsReuse = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILocalizationManager _localization;
    private readonly TmdbClient _tmdb;
    private readonly PicksStore _store;
    private readonly ILogger<PicksEngine> _logger;
    private readonly object _libraryIdsLock = new();
    private HashSet<string>? _libraryIds;
    private DateTime _libraryIdsReadUtc;
    private int _libraryIdsChanges;
    private int _libraryChanges;

    public PicksEngine(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IPlaylistManager playlistManager,
        ILocalizationManager localization,
        TmdbClient tmdb,
        PicksStore store,
        ILogger<PicksEngine> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playlistManager = playlistManager;
        _localization = localization;
        _tmdb = tmdb;
        _store = store;
        _logger = logger;

        // The engine lives as long as the server, so these are never removed.
        libraryManager.ItemAdded += OnLibraryChanged;
        libraryManager.ItemUpdated += OnLibraryChanged;
        libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <summary>Collect run-wide data once for the nightly run: the TMDb key and every TMDb id on the server, read afresh.</summary>
    public PicksRunContext CreateRunContext() => CreateRunContext(reuseLibraryIds: false);

    /// <summary>The same for one person's rebuild from the picks page, with the server's TMDb ids read lately if they still hold.</summary>
    public PicksRunContext CreateRebuildContext() => CreateRunContext(reuseLibraryIds: true);

    private PicksRunContext CreateRunContext(bool reuseLibraryIds)
    {
        var context = new PicksRunContext { TmdbKey = _store.GetTmdbKey() };

        // Only Discover uses these ids, so without it the library isn't read for them.
        if (Plugin.Instance?.Configuration is { EnableDiscover: false } || string.IsNullOrEmpty(context.TmdbKey))
        {
            return context;
        }

        // A copy, as each run's context is its own.
        context.LibraryTmdbIds.UnionWith(LibraryTmdbIds(reuseLibraryIds));
        return context;
    }

    private HashSet<string> LibraryTmdbIds(bool reuse)
    {
        // Held while reading, so rebuilds that start together read the library once between them.
        lock (_libraryIdsLock)
        {
            int changes = Volatile.Read(ref _libraryChanges);
            if (reuse && _libraryIds is not null && _libraryIdsChanges == changes && DateTime.UtcNow - _libraryIdsReadUtc < LibraryIdsReuse)
            {
                return _libraryIds;
            }

            // Every title on the server, not only this person's, so Discover doesn't offer what an admin keeps from them.
            var everything = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
                DtoOptions = Fields(providerIds: true)
            });

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in everything)
            {
                var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    ids.Add((item is Series ? "tv:" : "movie:") + tmdbId);
                }
            }

            _libraryIds = ids;
            _libraryIdsChanges = changes;
            _libraryIdsReadUtc = DateTime.UtcNow;
            return ids;
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            Interlocked.Increment(ref _libraryChanges);
        }
    }

    /// <summary>
    /// What a library read loads besides each title's own columns: TMDb and other provider ids where they're used, and
    /// play state only where it's read. Left to Jellyfin, every read would load all of them, with images and locked fields.
    /// </summary>
    private static DtoOptions Fields(bool providerIds, bool userData = false) => new(false)
    {
        Fields = providerIds ? new[] { ItemFields.ProviderIds } : Array.Empty<ItemFields>(),
        EnableImages = false,
        EnableUserData = userData
    };

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
        var language = AudioLanguage.FromTmdb(config.TmdbLanguage, _localization.FindLanguageInfo);
        var picks = new UserPicks { GeneratedUtc = DateTime.UtcNow, PlaylistId = previous?.PlaylistId };

        // The titles both kinds of library picks choose from, and the genres on the page, read once for this build.
        var candidates = new Candidates(_libraryManager, userQuery);
        picks.AvailableGenres = LibraryGenres(candidates);

        // 1. What has this user watched? Episodes roll up to their series.
        var watchedQuery = userQuery();
        watchedQuery.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode };
        watchedQuery.IsPlayed = true;
        watchedQuery.Recursive = true;
        watchedQuery.IsVirtualItem = false;
        watchedQuery.DtoOptions = Fields(providerIds: true, userData: true); // the last play comes from the user data
        var titles = RollUpWatched(
            _libraryManager.GetItemList(watchedQuery),
            id => _libraryManager.GetItemById(id),
            item => _userDataManager.GetUserData(user, item)?.LastPlayedDate);

        if (titles.Count < Math.Max(1, config.MinimumWatchedTitles))
        {
            picks.Note = "Watch a few more things and your picks will appear here after the next nightly run.";
            await ReplacePlaylistAsync(userId, picks, Array.Empty<Guid>(), config.PlaylistName, false).ConfigureAwait(false);
            _store.SaveWithoutHidden(userId, picks);
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
            picks.InLibrary = BuildLibraryPicks(userQuery, candidates, ranked, titles, libraryCount, prefs, language);
            if (picks.InLibrary.Count == 0 && prefs.SeenItems.Count > 0)
            {
                // "Show me different ones" has been through them all, so start again from the best.
                prefs.SeenItems.Clear();
                ForgetSeen(userId, items: true);
                picks.InLibrary = BuildLibraryPicks(userQuery, candidates, ranked, titles, libraryCount, prefs, language);
            }
        }

        // 2b. Titles from the same world as something watched.
        if (config.EnableLinkedPicks)
        {
            int linkedCount = prefs.Count > 0 ? prefs.Count : Math.Clamp(config.LibraryPickCount, 1, 100);
            var alreadyPicked = new HashSet<Guid>(picks.InLibrary.Select(p => p.ItemId));
            try
            {
                picks.Linked = BuildLinkedPicks(userQuery, candidates, ranked, titles, linkedCount, prefs, language, alreadyPicked);
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
        _store.SaveWithoutHidden(userId, picks);

        // 4. Discover picks from TMDb.
        var discoverAllowed = !string.IsNullOrEmpty(context.TmdbKey)
            && DiscoverAccess.Allowed(config, userId, DiscoverAccess.HasParentalLimits(user));

        int discoverCount = prefs.Count > 0 ? prefs.Count : Math.Clamp(config.DiscoverPickCount, 1, 100);
        if (!discoverAllowed)
        {
            picks.Discover = new List<DiscoverPick>();
        }
        else if (!context.TmdbUnavailable)
        {
            // When TMDb can't answer (now or earlier in this run), last run's Discover picks, kept above, stay.
            try
            {
                var discover = await BuildDiscoverPicksAsync(ranked, context, config.TmdbLanguage, discoverCount, prefs, cancellationToken).ConfigureAwait(false);
                if (discover.Count == 0 && prefs.SeenTmdb.Count > 0)
                {
                    // Everything recommended has been shown before: start the "seen" list again.
                    prefs.SeenTmdb.Clear();
                    ForgetSeen(userId, items: false);
                    discover = await BuildDiscoverPicksAsync(ranked, context, config.TmdbLanguage, discoverCount, prefs, cancellationToken).ConfigureAwait(false);
                }

                picks.Discover = discover;
            }
            catch (TmdbUnavailableException ex)
            {
                context.TmdbUnavailable = true;
                _logger.LogWarning(ex, "Medic Picks: TMDb isn't answering, so Discover picks were left as they were");
            }
        }

        _store.SaveWithoutHidden(userId, picks);
    }

    /// <summary>Each title watched, once: episodes count towards their series, and the latest play is kept.</summary>
    public static Dictionary<Guid, WatchedTitle> RollUpWatched(
        IEnumerable<BaseItem> watchedItems,
        Func<Guid, BaseItem?> getSeries,
        Func<BaseItem, DateTime?> lastPlayed)
    {
        var titles = new Dictionary<Guid, WatchedTitle>();
        var seriesCache = new Dictionary<Guid, BaseItem?>();

        foreach (var item in watchedItems)
        {
            BaseItem? title = item;
            if (item is Episode episode)
            {
                // A video at the root of a TV library is an episode of no series, and Jellyfin 12.2 throws when
                // asked for the empty id, which would end this person's whole build.
                if (episode.SeriesId == Guid.Empty)
                {
                    continue;
                }

                if (!seriesCache.TryGetValue(episode.SeriesId, out title))
                {
                    title = getSeries(episode.SeriesId);
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
            var played = lastPlayed(item);
            if (played.HasValue && (!watched.LastPlayed.HasValue || played > watched.LastPlayed))
            {
                watched.LastPlayed = played;
            }
        }

        return titles;
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
        Candidates candidates,
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
            pool.AddRange(candidates.Movies);
        }

        if (prefs.Kind != "movies")
        {
            pool.AddRange(candidates.Series);
        }

        var started = prefs.Kind != "movies" ? candidates.StartedSeries : new HashSet<Guid>();

        var hidden = new HashSet<Guid>(prefs.HiddenItems.Concat(prefs.SeenItems));
        var poolById = pool
            .Where(c => !titles.ContainsKey(c.Id) && !started.Contains(c.Id) && !hidden.Contains(c.Id) && !alreadyPicked.Contains(c.Id))
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

        // 1. Names: a series and its films, a film and its sequels. Looked up through an index of each name and
        // its leading words, built once, rather than comparing every title watched with every title in the pool.
        var poolNames = poolById.Values.Select(c => (Item: c, Key: NameKey(c.Name))).Where(x => x.Key.Length > 0).ToList();
        var byName = new Dictionary<string, List<(BaseItem Item, string Key)>>(StringComparer.Ordinal);
        var byLeadingWords = new Dictionary<string, List<(BaseItem Item, string Key)>>(StringComparer.Ordinal);
        foreach (var entry in poolNames)
        {
            Add(byName, entry.Key, entry);
            foreach (string lead in LeadingWords(entry.Key))
            {
                Add(byLeadingWords, lead, entry);
            }
        }

        foreach (var seed in ranked)
        {
            string key = NameKey(seed.Item.Name);
            if (key.Length == 0)
            {
                continue;
            }

            // Same name, a longer name starting with this one, or a shorter one this starts with.
            var matches = new HashSet<(BaseItem Item, string Key)>(byName.GetValueOrDefault(key) ?? new());
            matches.UnionWith(byLeadingWords.GetValueOrDefault(key) ?? new());
            foreach (string lead in LeadingWords(key))
            {
                matches.UnionWith(byName.GetValueOrDefault(lead) ?? new());
            }

            foreach (var (item, itemKey) in matches)
            {
                bool sameKind = item.GetType() == seed.Item.GetType();
                string shorter = itemKey.Length < key.Length ? itemKey : key;

                // Same name: a film and a series of it. One name starting with the other: a sequel or spin-off,
                // as long as the shorter name is distinctive. A one-word name ("Avatar") only links the same
                // kind, so the Avatar films don't pull in Avatar: The Last Airbender.
                bool linked = itemKey == key
                    ? !sameKind && Distinctive(key)
                    : Distinctive(shorter) && (shorter.Contains(' ', StringComparison.Ordinal) || sameKind);
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
        boxQuery.DtoOptions = Fields(providerIds: false);
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

    private static void Add<T>(Dictionary<string, List<T>> index, string key, T value)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<T>();
            index[key] = list;
        }

        list.Add(value);
    }

    /// <summary>
    /// What library picks choose from, each read from the library once per build and only when first needed:
    /// every film and series the person can see (for the genres on their page, and the series), the films they
    /// haven't watched or started, and the series with an episode under way.
    /// </summary>
    private sealed class Candidates
    {
        private readonly Lazy<List<BaseItem>> _titles;
        private readonly Lazy<List<BaseItem>> _movies;
        private readonly Lazy<List<BaseItem>> _series;
        private readonly Lazy<HashSet<Guid>> _started;

        public Candidates(ILibraryManager library, Func<InternalItemsQuery> userQuery)
        {
            _titles = new Lazy<List<BaseItem>>(() =>
            {
                var q = userQuery();
                q.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series };
                q.Recursive = true;
                q.IsVirtualItem = false;
                q.DtoOptions = Fields(providerIds: true);
                return library.GetItemList(q).ToList();
            });

            // Which films are watched or started is the server's to judge (alternate versions, say), so they're
            // asked for rather than worked out from every film above.
            _movies = new Lazy<List<BaseItem>>(() =>
            {
                var q = userQuery();
                q.IncludeItemTypes = new[] { BaseItemKind.Movie };
                q.IsPlayed = false;
                q.IsResumable = false; // not finished is not enough: started ones are left out too
                q.Recursive = true;
                q.IsVirtualItem = false;
                q.DtoOptions = Fields(providerIds: true);
                return library.GetItemList(q).ToList();
            });
            _series = new Lazy<List<BaseItem>>(() => _titles.Value.Where(t => t is Series).ToList());
            _started = new Lazy<HashSet<Guid>>(() =>
            {
                // A series with an episode under way has been started, even if no episode is finished yet.
                var q = userQuery();
                q.IncludeItemTypes = new[] { BaseItemKind.Episode };
                q.IsResumable = true;
                q.Recursive = true;
                q.IsVirtualItem = false;
                q.DtoOptions = Fields(providerIds: false);
                return library.GetItemList(q).OfType<Episode>().Select(e => e.SeriesId).ToHashSet();
            });
        }

        public List<BaseItem> Titles => _titles.Value;

        public List<BaseItem> Movies => _movies.Value;

        public List<BaseItem> Series => _series.Value;

        public HashSet<Guid> StartedSeries => _started.Value;
    }

    /// <summary>A name's leading words, shortest first, without the whole name: "a b c" gives "a" and "a b".</summary>
    public static IEnumerable<string> LeadingWords(string key)
    {
        for (int i = key.IndexOf(' ', StringComparison.Ordinal); i > 0; i = key.IndexOf(' ', i + 1))
        {
            yield return key[..i];
        }
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
    private List<string> LibraryGenres(Candidates candidates)
    {
        try
        {
            return candidates.Titles
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

    // Changed under the store's lock, so a title hidden while this run was going isn't lost.
    private void ForgetSeen(Guid userId, bool items) =>
        _store.UpdatePreferences(userId, fresh =>
        {
            if (items)
            {
                fresh.SeenItems.Clear();
            }
            else
            {
                fresh.SeenTmdb.Clear();
            }

            return fresh;
        });

    private List<LibraryPick> BuildLibraryPicks(Func<InternalItemsQuery> userQuery, Candidates pool, List<WatchedTitle> ranked, Dictionary<Guid, WatchedTitle> titles, int count, UserPreferences prefs, AudioLanguage language)
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

        // Candidates: movies the user hasn't watched or started, and series they haven't started.
        var candidates = new List<BaseItem>();
        if (prefs.Kind != "series")
        {
            candidates.AddRange(pool.Movies);
        }

        if (prefs.Kind != "movies")
        {
            candidates.AddRange(pool.Series.Where(s => !titles.ContainsKey(s.Id) && !pool.StartedSeries.Contains(s.Id)));
        }

        // The person's own choices: titles they hid, and the genres they picked.
        var hidden = new HashSet<Guid>(prefs.HiddenItems.Concat(prefs.SeenItems));
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
                return new Scored(c, genreScore + (0.1 * rating));
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
            // Unknown counts as not having the language.
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
        episodeQuery.DtoOptions = Fields(providerIds: false);
        var episodes = _libraryManager.GetItemList(episodeQuery);

        var first = episodes
            .Where(e => (e.ParentIndexNumber ?? 1) > 0)
            .OrderBy(e => e.ParentIndexNumber ?? 1)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .FirstOrDefault();

        return first?.Id;
    }

    /// <summary>
    /// Puts these items in the person's private playlist. Last run's playlist is updated in place, so it keeps its
    /// id (and its place in every app); a new one is made only when there's none, or the old one can't be updated.
    /// With nothing to put in it, or playlists switched off, the old one is deleted.
    /// </summary>
    private async Task ReplacePlaylistAsync(Guid userId, UserPicks picks, IReadOnlyList<Guid> items, string name, bool create)
    {
        string title = string.IsNullOrWhiteSpace(name) ? "Picks for you" : name.Trim();
        try
        {
            var old = picks.PlaylistId is { } oldId ? _libraryManager.GetItemById(oldId) as Playlist : null;
            if (old is not null && old.OwnerUserId != userId)
            {
                old = null; // never someone else's
            }

            if (old is not null && create && items.Count > 0)
            {
                try
                {
                    await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
                    {
                        Id = old.Id,
                        UserId = userId,
                        Name = title,
                        Ids = items.ToArray(),
                        Public = false
                    }).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Medic Picks: couldn't update the playlist for user {UserId} in place, so it's made again", userId);
                }
            }

            if (old is not null)
            {
                _libraryManager.DeleteItem(old, new DeleteOptions { DeleteFileLocation = true });
            }

            picks.PlaylistId = null;
            if (!create || items.Count == 0)
            {
                return;
            }

            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = title,
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

    // People with a playlist refresh waiting to start. A hide made while one waits is covered by it, because the
    // waiting refresh reads the picks only after it leaves this set.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> PendingRefreshes = new();

    /// <summary>How long a playlist refresh waits before it starts, so hides made meanwhile share it.</summary>
    internal TimeSpan PlaylistRefreshDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Puts the person's saved library picks in their playlist again, without a build: after they hide a title, so it
    /// leaves the playlist within seconds rather than at the next build. The refresh starts after
    /// <see cref="PlaylistRefreshDelay"/>, and one that is still waiting covers any hides made before it starts, so
    /// several quick hides cost one playlist update.
    /// </summary>
    public async Task RefreshPlaylistAsync(Guid userId)
    {
        if (!PendingRefreshes.TryAdd(userId, 0))
        {
            return;
        }

        await Task.Delay(PlaylistRefreshDelay).ConfigureAwait(false);
        var gate = UserLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Read only now: a build that held the gate has saved its picks and playlist, and a hide from here on
            // starts a refresh of its own.
            PendingRefreshes.TryRemove(userId, out _);
            var config = Plugin.Instance!.Configuration;
            var user = _userManager.GetUserById(userId);
            var picks = _store.Load(userId);
            if (user is null || picks is null || !(config.EnableLibraryPicks && config.CreatePlaylists))
            {
                return;
            }

            Func<InternalItemsQuery> userQuery = () => new InternalItemsQuery(user);
            var items = picks.InLibrary.Select(p => GetPlayableId(userQuery, p)).OfType<Guid>().ToList();
            Guid? before = picks.PlaylistId;
            await ReplacePlaylistAsync(userId, picks, items, config.PlaylistName, true).ConfigureAwait(false);
            if (picks.PlaylistId != before)
            {
                _store.SaveWithoutHidden(userId, picks);
            }
        }
        finally
        {
            gate.Release();
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
        var hidden = new HashSet<string>(prefs.HiddenTmdb.Concat(prefs.SeenTmdb), StringComparer.Ordinal);

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

    public sealed class WatchedTitle
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
        public Scored(BaseItem item, double score)
        {
            Item = item;
            Score = score;
        }

        public BaseItem Item { get; }

        public double Score { get; set; }

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

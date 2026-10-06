using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>Everything stored for one user after a run.</summary>
public class UserPicks
{
    public DateTime GeneratedUtc { get; set; }

    /// <summary>Plain-language note shown on the page when there's nothing to show yet.</summary>
    public string? Note { get; set; }

    /// <summary>Id of the private playlist created last run, so it can be replaced next run.</summary>
    public Guid? PlaylistId { get; set; }

    public List<LibraryPick> InLibrary { get; set; } = new();

    public List<DiscoverPick> Discover { get; set; } = new();

    /// <summary>Genres of the films and series this person can see, for the genre choices on their page.</summary>
    public List<string> AvailableGenres { get; set; } = new();
}

/// <summary>A title that's already on the server.</summary>
public class LibraryPick
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>"Movie" or "Series".</summary>
    public string Kind { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

/// <summary>A title that isn't on the server yet.</summary>
public class DiscoverPick
{
    public int TmdbId { get; set; }

    /// <summary>"movie" or "tv" (TMDb's own terms, used to build links).</summary>
    public string MediaType { get; set; } = "movie";

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public double Rating { get; set; }

    /// <summary>Title the user watched that led to this pick.</summary>
    public string BecauseOf { get; set; } = string.Empty;

    /// <summary>Japanese animation, by TMDb's original language and genre.</summary>
    public bool IsAnime { get; set; }

    /// <summary>TMDb's original language, e.g. "en" or "ja".</summary>
    public string? OriginalLanguage { get; set; }
}

/// <summary>A recommendation as returned by TMDb.</summary>
public class TmdbTitle
{
    public int Id { get; set; }

    public string MediaType { get; set; } = "movie";

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public double VoteAverage { get; set; }

    public string? OriginalLanguage { get; set; }

    public List<int> GenreIds { get; set; } = new();
}

/// <summary>Choices a person makes on their own My picks page.</summary>
public class UserPreferences
{
    /// <summary>How many picks each section may show.</summary>
    public static readonly int[] CountChoices = { 5, 10, 15, 20, 25, 30 };

    /// <summary>"all", "movies" or "series".</summary>
    public string Kind { get; set; } = "all";

    /// <summary>Only these genres. Empty means any genre.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>How many picks per section: one of <see cref="CountChoices"/>, or 0 for the server's default.</summary>
    public int Count { get; set; }

    /// <summary>Only suggest titles with audio in the server's language (on the server); flag ones that may not have it (not on the server).</summary>
    public bool DubbedOnly { get; set; }

    /// <summary>Titles this person said they're not interested in, by Jellyfin item ID.</summary>
    public List<Guid> HiddenItems { get; set; } = new();

    /// <summary>Titles not on the server they're not interested in, as "movie:123" or "tv:456".</summary>
    public List<string> HiddenTmdb { get; set; } = new();

    /// <summary>Older setting ("Anime: English dubs only"), read once and turned into <see cref="DubbedOnly"/>.</summary>
    public bool? EnglishDubAnime { get; set; }

    /// <summary>Brings older saved choices up to date and keeps values in range.</summary>
    public UserPreferences Normalise()
    {
        if (EnglishDubAnime == true)
        {
            DubbedOnly = true;
        }

        EnglishDubAnime = null;
        Kind = Kind is "movies" or "series" ? Kind : "all";
        Genres = (Genres ?? new List<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g) && g.Length <= 60)
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
        // Round anything else to the nearest choice (0 stays "the server's default").
        Count = Count <= 0 ? 0 : CountChoices.OrderBy(c => Math.Abs(c - Count)).First();
        HiddenItems = (HiddenItems ?? new List<Guid>()).Distinct().TakeLast(1000).ToList();
        HiddenTmdb = (HiddenTmdb ?? new List<string>()).Distinct(StringComparer.Ordinal).TakeLast(1000).ToList();
        return this;
    }
}

/// <summary>Shared state for one run of the task across all users.</summary>
public class PicksRunContext
{
    public string? TmdbKey { get; set; }

    /// <summary>TMDb ids of everything on the server (movies and series), so Discover never suggests what's already there.</summary>
    public HashSet<string> LibraryTmdbIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TMDb results cached across users, keyed "movie:123" / "tv:456".</summary>
    public Dictionary<string, IReadOnlyList<TmdbTitle>> TmdbCache { get; } = new(StringComparer.Ordinal);
}

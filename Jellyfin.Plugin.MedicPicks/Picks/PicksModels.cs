using System;
using System.Collections.Generic;

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
    /// <summary>Only suggest anime from the library that has English audio.</summary>
    public bool EnglishDubAnime { get; set; }
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

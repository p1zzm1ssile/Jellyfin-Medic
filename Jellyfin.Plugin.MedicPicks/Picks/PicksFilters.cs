using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Genre matching. TV genres often come in pairs ("Sci-Fi &amp; Fantasy", "Action &amp; Adventure"), so
/// each genre is expanded into the names it covers: choosing Science Fiction also finds Sci-Fi &amp; Fantasy series.
/// </summary>
public static class Genres
{
    // TMDb's genre IDs (films and TV), for titles that aren't on the server yet.
    private static readonly Dictionary<int, string> TmdbGenres = new()
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime",
        [99] = "Documentary", [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History",
        [27] = "Horror", [10402] = "Music", [9648] = "Mystery", [10749] = "Romance", [878] = "Science Fiction",
        [10770] = "TV Movie", [53] = "Thriller", [10752] = "War", [37] = "Western",
        [10759] = "Action & Adventure", [10762] = "Kids", [10763] = "News", [10764] = "Reality",
        [10765] = "Sci-Fi & Fantasy", [10766] = "Soap", [10767] = "Talk", [10768] = "War & Politics"
    };

    public static string TmdbName(int id) => TmdbGenres.TryGetValue(id, out var name) ? name : string.Empty;

    /// <summary>Each genre plus the names it covers, lower case: "Sci-Fi &amp; Fantasy" gives sci-fi &amp; fantasy, sci-fi, science fiction and fantasy.</summary>
    public static HashSet<string> Expand(IEnumerable<string>? genres)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in genres ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(genre))
            {
                continue;
            }

            string g = genre.Trim();
            all.Add(g);
            foreach (var part in g.Split(new[] { " & ", " and ", "/" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                all.Add(part);
            }
        }

        if (all.Contains("Sci-Fi") || all.Contains("SciFi"))
        {
            all.Add("Science Fiction");
        }

        if (all.Contains("Science Fiction"))
        {
            all.Add("Sci-Fi");
        }

        if (all.Contains("Kids"))
        {
            all.Add("Family");
        }

        return all;
    }
}

/// <summary>The language "dubbed audio" means: the one picks are shown in (from the TMDb language setting).</summary>
public sealed class AudioLanguage
{
    private static readonly Dictionary<string, (string Name, string[] Codes)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ("English", new[] { "en", "eng", "english" }),
        ["fr"] = ("French", new[] { "fr", "fre", "fra", "french" }),
        ["de"] = ("German", new[] { "de", "ger", "deu", "german" }),
        ["es"] = ("Spanish", new[] { "es", "spa", "spanish" }),
        ["it"] = ("Italian", new[] { "it", "ita", "italian" }),
        ["nl"] = ("Dutch", new[] { "nl", "dut", "nld", "dutch" }),
        ["pt"] = ("Portuguese", new[] { "pt", "por", "portuguese" }),
        ["sv"] = ("Swedish", new[] { "sv", "swe", "swedish" }),
        ["da"] = ("Danish", new[] { "da", "dan", "danish" }),
        ["no"] = ("Norwegian", new[] { "no", "nor", "nob", "nno", "norwegian" }),
        ["fi"] = ("Finnish", new[] { "fi", "fin", "finnish" }),
        ["pl"] = ("Polish", new[] { "pl", "pol", "polish" }),
        ["ru"] = ("Russian", new[] { "ru", "rus", "russian" }),
        ["ja"] = ("Japanese", new[] { "ja", "jpn", "japanese" }),
        ["ko"] = ("Korean", new[] { "ko", "kor", "korean" }),
        ["zh"] = ("Chinese", new[] { "zh", "chi", "zho", "chinese" })
    };

    private readonly HashSet<string> _codes;

    private AudioLanguage(string twoLetter, string name, IEnumerable<string> codes)
    {
        TwoLetter = twoLetter;
        Name = name;
        _codes = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>e.g. "en".</summary>
    public string TwoLetter { get; }

    /// <summary>e.g. "English".</summary>
    public string Name { get; }

    /// <summary>From a TMDb language such as "en-GB". Unknown languages fall back to English.</summary>
    public static AudioLanguage FromTmdb(string? tmdbLanguage)
    {
        string two = (tmdbLanguage ?? "en").Split('-')[0].Trim().ToLowerInvariant();
        return Known.TryGetValue(two, out var lang)
            ? new AudioLanguage(two, lang.Name, lang.Codes)
            : new AudioLanguage("en", "English", Known["en"].Codes);
    }

    /// <summary>True when a track's language tag (any common form) is this language.</summary>
    public bool Matches(string? tag) => !string.IsNullOrWhiteSpace(tag) && _codes.Contains(tag.Trim());
}

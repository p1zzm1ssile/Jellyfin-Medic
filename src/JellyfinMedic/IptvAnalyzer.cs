using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JellyfinMedic.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

public class IptvGroup
{
    public string Name { get; set; } = string.Empty;

    public long Items { get; set; }

    // Most items in this group watched by any one person (a lower bound for "watched at all").
    public long Watched { get; set; }

    public double Share { get; set; }
}

public class IptvLibrary
{
    public string Name { get; set; } = string.Empty;

    public long Items { get; set; }

    public List<IptvGroup> Genres { get; set; } = new();

    public List<IptvGroup> Countries { get; set; } = new();
}

public class ChannelReport
{
    public int Total { get; set; }

    public int DuplicateGroups { get; set; }

    public int ExtraCopies { get; set; }

    public int Regional { get; set; }

    public List<string> Examples { get; set; } = new();

    // Every channel shown more than once, with the names of each copy (largest groups first, up to 500).
    public List<DuplicateChannel> Duplicates { get; set; } = new();
}

public class DuplicateChannel
{
    public string Name { get; set; } = string.Empty;

    public List<string> Versions { get; set; } = new();
}

public class IptvReport
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public List<IptvLibrary> Libraries { get; set; } = new();

    public ChannelReport Channels { get; set; } = new();
}

/// <summary>
/// Looks at what your IPTV libraries contain and how much of it gets watched, so you can untick
/// categories nobody uses in Xtream Library, and spots channels that appear several times in
/// different qualities. Runs only when asked (it reads a lot), and the result is kept for a day.
/// </summary>
public static class IptvAnalyzer
{
    private static readonly string[] QualityWords = { "uhd", "fhd", "hd", "sd", "4k", "8k", "hevc", "h265", "h264", "raw", "backup", "alt", "vip", "50fps", "60fps", "hdr" };
    private static readonly string[] Regions =
    {
        "west midlands", "east midlands", "yorkshire", "north west", "north east", "south west", "south east",
        "london", "wales", "scotland", "ulster", "anglia", "central", "granada", "tyne tees", "meridian", "border", "channel islands"
    };

    private static readonly Regex Prefix = new(@"^\s*[\[\(|]?\s*[a-z]{2,3}\s*[\]\)|:\-]\s*", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"[\[\(][^\]\)]*[\]\)]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static IptvReport? Last { get; private set; }

    public static IptvReport Analyse(ILibraryManager library, IUserManager users, List<LibraryFacts> libraries)
    {
        var report = new IptvReport();
        var people = TopUsers(users, 5);

        foreach (var lib in libraries.Where(l => l.IsStreamed))
        {
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            var entry = new IptvLibrary { Name = lib.Name, Items = lib.ItemCount ?? 0 };
            var sample = Safe(() => library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false, Limit = 2000 }).Cast<object>().ToList()) ?? new List<object>();

            var genres = Count(sample, "Genres").Take(10).ToList();
            foreach (var (name, hits) in genres)
            {
                long items = SafeCount(() => (long)library.GetCount(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false, Genres = new[] { name } })) ?? 0;
                long watched = 0;
                foreach (var person in people)
                {
                    var query = new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false, Genres = new[] { name }, IsPlayed = true };
                    if (SetUser(query, person))
                    {
                        watched = Math.Max(watched, SafeCount(() => (long)library.GetCount(query)) ?? 0);
                    }
                }

                entry.Genres.Add(new IptvGroup { Name = name, Items = items, Watched = watched, Share = sample.Count == 0 ? 0 : Math.Round((double)hits / sample.Count, 3) });
            }

            entry.Countries = Count(sample, "ProductionLocations").Take(8)
                .Select(c => new IptvGroup { Name = c.Name, Share = sample.Count == 0 ? 0 : Math.Round((double)c.Hits / sample.Count, 3), Items = (long)Math.Round(entry.Items * (sample.Count == 0 ? 0 : (double)c.Hits / sample.Count)) })
                .ToList();

            report.Libraries.Add(entry);
        }

        report.Channels = Channels(library);
        Last = report;
        return report;
    }

    public static List<Finding> Findings()
    {
        var findings = new List<Finding>();
        if (Last is not { } report)
        {
            return findings;
        }

        if (report.Channels.ExtraCopies >= 30)
        {
            findings.Add(new Finding
            {
                Area = "IPTV",
                Severity = Sev.Tip,
                Title = $"{report.Channels.ExtraCopies:N0} live channels are extra copies of others",
                Current = $"{report.Channels.DuplicateGroups:N0} channels appear in several qualities or versions, e.g. {string.Join(", ", report.Channels.Examples.Take(3))}",
                Recommended = "Keep one version of each channel (usually the best quality you can play) and hide or remove the rest",
                Why = "Duplicates make the guide long and slow to refresh, and make channels harder to find. Medic → IPTV lists every channel with copies and the name of each one, so you can see which to drop.",
                Where = "Medic → IPTV → Live channels, then your IPTV plugin's channel or category settings"
            });
        }

        foreach (var lib in report.Libraries)
        {
            var unwatched = lib.Genres.Where(g => g.Items >= 500 && g.Watched == 0).Take(4).ToList();
            if (unwatched.Count > 0)
            {
                findings.Add(new Finding
                {
                    Area = "IPTV",
                    Severity = Sev.Tip,
                    Title = $"{lib.Name}: big genres nobody watches",
                    Current = string.Join(", ", unwatched.Select(g => $"{g.Name} ({g.Items:N0} items)")),
                    Recommended = "Untick the matching categories in Xtream Library",
                    Why = "Every item makes scans, list pages and searches slower. If nobody watches a genre, importing it costs speed for nothing.",
                    Where = "Dashboard → Plugins → Xtream Library → categories"
                });
            }
        }

        return findings;
    }

    private static ChannelReport Channels(ILibraryManager library)
    {
        var result = new ChannelReport();
        var query = new InternalItemsQuery { Recursive = true };
        var prop = typeof(InternalItemsQuery).GetProperty("IncludeItemTypes");
        var elementType = prop?.PropertyType.GetElementType();
        if (prop is null || elementType is null || !elementType.IsEnum || !Enum.IsDefined(elementType, "LiveTvChannel"))
        {
            return result;
        }

        var kinds = Array.CreateInstance(elementType, 1);
        kinds.SetValue(Enum.Parse(elementType, "LiveTvChannel"), 0);
        prop.SetValue(query, kinds);

        var names = Safe(() => library.GetItemList(query).Select(i => i.Name ?? string.Empty).ToList()) ?? new List<string>();
        result.Total = names.Count;

        var groups = names.GroupBy(Normalise).Where(g => g.Key.Length > 1 && g.Count() > 1).OrderByDescending(g => g.Count()).ToList();
        result.DuplicateGroups = groups.Count;
        result.ExtraCopies = groups.Sum(g => g.Count() - 1);
        result.Examples = groups.Take(5).Select(g => $"{g.First()} ({g.Count()} versions)").ToList();
        result.Duplicates = groups.Take(500)
            .Select(g => new DuplicateChannel { Name = g.First(), Versions = g.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() })
            .ToList();
        result.Regional = names.Count(n => Regions.Any(r => n.Contains(r, StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    private static string Normalise(string name)
    {
        string text = name.ToLowerInvariant();
        text = Prefix.Replace(text, string.Empty);
        text = Brackets.Replace(text, " ");
        text = text.Replace("ᴴᴰ", " ").Replace("ᶠᴴᴰ", " ").Replace("ᵁᴴᴰ", " ");
        var words = Spaces.Split(text).Where(w => w.Length > 0 && !QualityWords.Contains(w.Trim('|', '-', ':', '+', '.'))).ToList();
        return string.Join(' ', words).Trim(' ', '|', '-', ':');
    }

    private static IEnumerable<(string Name, int Hits)> Count(List<object> items, string property) =>
        items.SelectMany(i => SettingsReader.List(i, property) ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2);

    private static List<object> TopUsers(IUserManager users, int max)
    {
        try
        {
            return UserList.All(users)
                .Where(u => !(SettingsReader.Bool(u, "Permissions.IsDisabled") ?? false))
                .OrderByDescending(u => SettingsReader.Get(u, "LastActivityDate") as DateTime? ?? DateTime.MinValue)
                .Take(max)
                .ToList();
        }
        catch
        {
            return new List<object>();
        }
    }

    private static bool SetUser(InternalItemsQuery query, object user)
    {
        var prop = typeof(InternalItemsQuery).GetProperty("User");
        if (prop is null || !prop.PropertyType.IsInstanceOfType(user))
        {
            return false;
        }

        prop.SetValue(query, user);
        return true;
    }

    private static T? Safe<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static long? SafeCount(Func<long> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinMedic.Services;

public class DirectoryPlugin
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new(); // Stale, Beta, Paid, Official
}

public class DirectoryCategory
{
    public string Name { get; set; } = string.Empty;

    public List<DirectoryPlugin> Plugins { get; set; } = new();
}

public class PluginDirectory
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public List<DirectoryCategory> Categories { get; set; } = new();

    public string Source { get; set; } = "awesome-jellyfin";

    public string SourceUrl { get; set; } = "https://github.com/awesome-jellyfin/awesome-jellyfin";

    public string? Error { get; set; }
}

/// <summary>
/// The community plugin list from the awesome-jellyfin project, fetched live from their README so
/// it's always current, and always credited to them. Read-only; Medic just displays it and links
/// out to each plugin's own page. Cached for 6 hours so it isn't fetched on every visit.
/// </summary>
public static class PluginDirectoryService
{
    private const string Raw = "https://raw.githubusercontent.com/awesome-jellyfin/awesome-jellyfin/main/README.md";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTime At, PluginDirectory Data)? _cache;

    // Lines like:  - [Name](url) - description `🔸 Stale`
    private static readonly Regex Item = new(@"^\s*[-*]\s*\[(?<name>[^\]]+)\]\((?<url>[^)]+)\)\s*[-–—]?\s*(?<desc>.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(?<hashes>#{2,4})\s*(?<title>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"`[^`]*?(Stale|Beta|Paid|Official)[^`]*?`", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Emoji = new(@"[\p{So}\p{Cs}\uFE0F]", RegexOptions.Compiled);
    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);

    public static async Task<PluginDirectory> GetAsync(CancellationToken ct)
    {
        if (_cache is { } hit && DateTime.UtcNow - hit.At < TimeSpan.FromHours(6))
        {
            return hit.Data;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.At < TimeSpan.FromHours(6))
            {
                return again.Data;
            }

            var directory = new PluginDirectory();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Raw);
                request.Headers.UserAgent.ParseAdd("JellyfinMedic/1.0 (Jellyfin plugin)");
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string markdown = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Parse(markdown, directory);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                directory.Error = "Couldn't reach the awesome-jellyfin list. Open it directly at the link, or try again later.";
            }

            _cache = (DateTime.UtcNow, directory);
            return directory;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Parse(string markdown, PluginDirectory directory)
    {
        // Only the Plugins section (between "## 🧩 Plugins" and the next top "## " heading) is actual
        // installable Jellyfin plugins; the rest are apps, tools and guides, so we stop at the next section.
        var lines = markdown.Replace("\r", string.Empty).Split('\n');
        bool inPlugins = false;
        DirectoryCategory? current = null;

        foreach (var raw in lines)
        {
            var headMatch = Heading.Match(raw);
            if (headMatch.Success)
            {
                string title = Clean(headMatch.Groups["title"].Value);
                int level = headMatch.Groups["hashes"].Value.Length;

                if (level == 2)
                {
                    // Enter on the Plugins section, leave on the next level-2 heading.
                    inPlugins = title.Contains("Plugins", StringComparison.OrdinalIgnoreCase)
                                && !title.Contains("Companion", StringComparison.OrdinalIgnoreCase);
                    current = null;
                    continue;
                }

                if (inPlugins && level >= 3)
                {
                    current = new DirectoryCategory { Name = title };
                    directory.Categories.Add(current);
                }

                continue;
            }

            if (!inPlugins || current is null)
            {
                continue;
            }

            var itemMatch = Item.Match(raw);
            if (!itemMatch.Success)
            {
                continue;
            }

            string url = itemMatch.Groups["url"].Value.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string descRaw = itemMatch.Groups["desc"].Value;
            var tags = Tag.Matches(descRaw).Select(m => Capitalise(m.Groups[1].Value)).Distinct().ToList();
            current.Plugins.Add(new DirectoryPlugin
            {
                Name = Clean(itemMatch.Groups["name"].Value),
                Url = url,
                Description = Clean(Tag.Replace(descRaw, string.Empty)),
                Tags = tags
            });
        }

        // Drop any empty categories the parser created.
        directory.Categories = directory.Categories.Where(c => c.Plugins.Count > 0).ToList();
    }

    private static string Clean(string text)
    {
        string t = MdLink.Replace(text, "$1");   // turn any inline [text](link) into just text
        t = t.Replace("`", string.Empty);
        t = Emoji.Replace(t, string.Empty);
        return Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '–', '—', '.', ':');
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}

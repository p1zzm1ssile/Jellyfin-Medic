using System.Collections;
using System.Globalization;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

/// <summary>What the plugin suggestions looked at. Counts only: nothing about individual users is returned.</summary>
public sealed class PluginServerProfile
{
    public int Users { get; set; }

    public int Movies { get; set; }

    public int Series { get; set; }

    public int Episodes { get; set; }

    public int AudioTracks { get; set; }

    public int Books { get; set; }

    public int AnimeSeries { get; set; }

    // Films and episodes marked as watched, added up across every user.
    public int MoviePlays { get; set; }

    public int EpisodePlays { get; set; }

    public List<string> InstalledPlugins { get; set; } = new();

    // False when the plugin repositories couldn't be reached.
    public bool RepositoriesReachable { get; set; }

    public double EpisodeShare => MoviePlays + EpisodePlays == 0 ? 0 : (double)EpisodePlays / (MoviePlays + EpisodePlays);
}

public sealed class PluginSuggestion
{
    public string Plugin { get; set; } = string.Empty;

    // "Add", "Consider" or "Remove".
    public string Action { get; set; } = string.Empty;

    // "High", "Medium" or "Low".
    public string Priority { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    // Where to get it, e.g. "In your plugin catalogue". Empty for removals.
    public string Availability { get; set; } = string.Empty;

    public bool InCatalogue { get; set; }
}

public sealed class PluginAdvice
{
    public DateTime GeneratedUtc { get; set; }

    public PluginServerProfile Profile { get; set; } = new();

    public List<PluginSuggestion> Suggestions { get; set; } = new();
}

/// <summary>
/// Suggests plugins to add, consider or remove, from what's in the libraries, what gets watched,
/// and which plugins are already installed. Read-only: it never installs or removes anything.
/// Rules are plain data at the bottom of the file, so new ones are one entry each.
/// </summary>
public static class PluginAdvisor
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTime At, List<string> Names)? _catalogue;

    public static async Task<PluginAdvice> BuildAsync(
        ILibraryManager library,
        IUserManager users,
        IPluginManager plugins,
        IInstallationManager installs,
        CancellationToken ct)
    {
        var profile = BuildProfile(library, users, plugins);
        var catalogue = await CatalogueAsync(installs, ct).ConfigureAwait(false);
        profile.RepositoriesReachable = catalogue is not null;

        var suggestions = new List<PluginSuggestion>();
        foreach (var rule in Rules)
        {
            bool applies;
            try
            {
                applies = rule.When(profile);
            }
            catch
            {
                continue;
            }

            if (!applies)
            {
                continue;
            }

            string? installed = profile.InstalledPlugins.FirstOrDefault(n => ContainsAny(n, rule.Installed));

            if (rule.Action == "Remove")
            {
                if (installed is not null)
                {
                    suggestions.Add(new PluginSuggestion
                    {
                        Plugin = installed,
                        Action = "Remove",
                        Priority = rule.Priority,
                        Reason = rule.Reason(profile)
                    });
                }

                continue;
            }

            if (installed is not null)
            {
                continue; // already covered
            }

            bool inCatalogue = catalogue?.Any(n => ContainsAny(n, rule.Catalogue)) ?? false;
            suggestions.Add(new PluginSuggestion
            {
                Plugin = rule.Plugin,
                Action = rule.Action,
                Priority = rule.Priority,
                Reason = rule.Reason(profile),
                InCatalogue = inCatalogue,
                Availability = catalogue is null
                    ? "Couldn't check your plugin catalogue just now."
                    : inCatalogue
                        ? "In your plugin catalogue: Dashboard → Plugins → Catalog."
                        : rule.ThirdParty
                            ? "Not in your catalogue. It's published in its own repository."
                            : "Not in your catalogue. Check your plugin repositories."
            });
        }

        return new PluginAdvice
        {
            GeneratedUtc = DateTime.UtcNow,
            Profile = profile,
            Suggestions = suggestions
                .OrderBy(s => s.Action == "Remove" ? 1 : 0)
                .ThenBy(s => PriorityOrder(s.Priority))
                .ThenBy(s => s.Plugin, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    // ---------- What the server looks like ----------

    private static PluginServerProfile BuildProfile(ILibraryManager library, IUserManager users, IPluginManager plugins)
    {
        var profile = new PluginServerProfile
        {
            Movies = Count(library, null, null, "Movie"),
            Series = Count(library, null, null, "Series"),
            Episodes = Count(library, null, null, "Episode"),
            AudioTracks = Count(library, null, null, "Audio"),
            Books = Count(library, null, null, "Book", "AudioBook"),
            InstalledPlugins = plugins.Plugins.Cast<object>()
                .Select(p => SettingsReader.Text(p, "Name") ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList()
        };

        // Anime: series tagged "Anime", or series in a library whose name says anime.
        profile.AnimeSeries = Count(library, q => q.Genres = new[] { "Anime" }, null, "Series");
        try
        {
            foreach (var folder in library.GetVirtualFolders().Cast<object>())
            {
                string name = SettingsReader.Text(folder, "Name") ?? string.Empty;
                if (name.Contains("anime", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(SettingsReader.Text(folder, "ItemId"), out var id))
                {
                    profile.AnimeSeries = Math.Max(profile.AnimeSeries, Count(library, q => q.AncestorIds = new[] { id }, null, "Series"));
                }
            }
        }
        catch
        {
            // Library list unavailable: the anime check relies on genres alone.
        }

        // Viewing habits: watched films and episodes, added up across users.
        var everyone = UserList.All(users);
        profile.Users = everyone.Count;
        foreach (var user in everyone)
        {
            profile.MoviePlays += Count(library, q => q.IsPlayed = true, user, "Movie");
            profile.EpisodePlays += Count(library, q => q.IsPlayed = true, user, "Episode");
        }

        return profile;
    }

    /// <summary>
    /// Counts items of the named kinds, optionally as seen by one user. Returns 0 if this Jellyfin
    /// doesn't know a kind, or if a per-user count can't be scoped to that user (so it never
    /// silently counts the whole library instead).
    /// </summary>
    private static int Count(ILibraryManager library, Action<InternalItemsQuery>? extra, object? user, params string[] kinds)
    {
        try
        {
            var query = new InternalItemsQuery { Recursive = true, IsVirtualItem = false };
            if (!SetKinds(query, kinds))
            {
                return 0;
            }

            if (user is not null && !ForUser(query, user))
            {
                return 0;
            }

            extra?.Invoke(query);
            return library.GetCount(query);
        }
        catch
        {
            return 0;
        }
    }

    private static bool SetKinds(InternalItemsQuery query, string[] kinds)
    {
        var prop = typeof(InternalItemsQuery).GetProperty("IncludeItemTypes");
        var element = prop?.PropertyType.GetElementType();
        if (prop is null || element is null || !element.IsEnum)
        {
            return false;
        }

        var known = kinds.Where(k => Enum.IsDefined(element, k)).ToList();
        if (known.Count == 0)
        {
            return false;
        }

        var values = Array.CreateInstance(element, known.Count);
        for (int i = 0; i < known.Count; i++)
        {
            values.SetValue(Enum.Parse(element, known[i]), i);
        }

        prop.SetValue(query, values);
        return true;
    }

    private static bool ForUser(InternalItemsQuery query, object user)
    {
        var prop = typeof(InternalItemsQuery).GetProperty("User");
        if (prop is null || !prop.PropertyType.IsInstanceOfType(user))
        {
            return false;
        }

        prop.SetValue(query, user);
        return true;
    }

    // ---------- Plugin catalogue (kept for an hour) ----------

    private static async Task<List<string>?> CatalogueAsync(IInstallationManager installs, CancellationToken ct)
    {
        if (_catalogue is { } hit && DateTime.UtcNow - hit.At < TimeSpan.FromHours(1))
        {
            return hit.Names;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalogue is { } again && DateTime.UtcNow - again.At < TimeSpan.FromHours(1))
            {
                return again.Names;
            }

            var method = installs.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "GetAvailablePackages" && m.GetParameters().Length <= 1);
            if (method is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            object?[] args = method.GetParameters().Length == 1 ? new object?[] { timeout.Token } : Array.Empty<object?>();
            if (method.Invoke(installs, args) is not Task task)
            {
                return null;
            }

            await task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (task.GetType().GetProperty("Result")?.GetValue(task) is not IEnumerable items)
            {
                return null;
            }

            var names = items.Cast<object>()
                .Select(p => SettingsReader.Text(p, "Name") ?? string.Empty)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _catalogue = (DateTime.UtcNow, names);
            return names;
        }
        catch
        {
            return _catalogue?.Names;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---------- Helpers ----------

    private static bool ContainsAny(string value, IEnumerable<string> keywords) =>
        keywords.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static int PriorityOrder(string priority) => priority switch
    {
        "High" => 0,
        "Medium" => 1,
        _ => 2
    };

    private static string Pct(double share) => Math.Round(share * 100).ToString(CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-GB"));

    // ---------- Rules ----------

    private sealed record Rule(
        string Plugin,
        string[] Installed,
        string[] Catalogue,
        string Action,
        string Priority,
        Func<PluginServerProfile, bool> When,
        Func<PluginServerProfile, string> Reason,
        bool ThirdParty = false);

    private static readonly string[] AnimeProviders = { "anilist", "anidb", "kitsu", "anisearch" };
    private static readonly string[] BookProviders = { "openlibrary", "open library", "googlebooks", "google books", "comicvine", "comic vine" };
    private static readonly string[] IntroSkippers = { "intro skipper", "introskipper", "intro-skipper" };

    private static readonly Rule[] Rules =
    {
        // ---- Worth adding ----
        new(
            "Intro Skipper",
            IntroSkippers,
            new[] { "intro skipper" },
            "Add",
            "High",
            p => p.EpisodePlays >= 25 && p.EpisodeShare >= 0.5,
            p => $"About {Pct(p.EpisodeShare)}% of what's watched here is TV. Intro Skipper finds intros and credits so viewers can skip them.",
            ThirdParty: true),

        new(
            "AniList",
            AnimeProviders,
            new[] { "anilist" },
            "Add",
            "High",
            p => p.AnimeSeries >= 3,
            p => $"{N(p.AnimeSeries)} series look like anime. An anime metadata provider gets titles, seasons and artwork right far more often than TMDb alone."),

        new(
            "OpenLibrary",
            BookProviders,
            new[] { "openlibrary", "open library" },
            "Add",
            "Medium",
            p => p.Books >= 1,
            p => $"You have {N(p.Books)} books. Jellyfin 12 reads book files itself, and the OpenLibrary, Google Books or ComicVine providers fill in missing details."),

        new(
            "LrcLib",
            new[] { "lrclib", "lyrics" },
            new[] { "lrclib" },
            "Add",
            "Medium",
            p => p.AudioTracks >= 50,
            p => $"{N(p.AudioTracks)} music tracks. LrcLib fetches synced lyrics for the music player."),

        new(
            "Playback Reporting",
            new[] { "playback reporting" },
            new[] { "playback reporting" },
            "Add",
            "Medium",
            p => p.Users >= 2,
            p => $"{N(p.Users)} people use this server. Playback Reporting shows who watches what, when, and on which device."),

        new(
            "Open Subtitles",
            new[] { "opensubtitles", "open subtitles", "subtitle" },
            new[] { "open subtitles", "opensubtitles" },
            "Add",
            "Low",
            p => p.Movies + p.Episodes >= 50,
            p => "No subtitle downloader is installed. Open Subtitles fetches missing subtitles (it needs a free account)."),

        new(
            "TheTVDB",
            new[] { "tvdb" },
            new[] { "tvdb" },
            "Consider",
            "Low",
            p => p.Series >= 20,
            p => $"{N(p.Series)} series. TMDb covers most shows; TheTVDB is only worth adding if you need DVD or absolute episode order."),

        new(
            "Fanart",
            new[] { "fanart" },
            new[] { "fanart" },
            "Consider",
            "Low",
            p => p.Movies + p.Series >= 200,
            p => "Adds logos, clear art and extra backdrops, which many apps use on their home screens."),

        new(
            "LDAP Authentication",
            new[] { "ldap", "sso" },
            new[] { "ldap" },
            "Consider",
            "Low",
            p => p.Users >= 10,
            p => $"{N(p.Users)} accounts. If you already run a directory or identity provider, signing in through it saves managing passwords twice."),

        // ---- Probably not needed ----
        new(
            "Bookshelf",
            new[] { "bookshelf" },
            Array.Empty<string>(),
            "Remove",
            "High",
            p => true,
            p => "Bookshelf is deprecated in Jellyfin 12. Its features are now built in, with Google Books and ComicVine as separate providers."),

        new(
            "Anime provider",
            AnimeProviders,
            Array.Empty<string>(),
            "Remove",
            "Low",
            p => p.AnimeSeries == 0,
            p => "No anime found in your libraries, so this provider has nothing to do."),

        new(
            "Lyrics provider",
            new[] { "lrclib" },
            Array.Empty<string>(),
            "Remove",
            "Low",
            p => p.AudioTracks == 0,
            p => "There's no music on this server, so this provider has nothing to do."),

        new(
            "Intro Skipper",
            IntroSkippers,
            Array.Empty<string>(),
            "Remove",
            "Low",
            p => p.Episodes == 0,
            p => "There are no TV episodes on this server, so it has nothing to scan."),

        new(
            "Book provider",
            BookProviders,
            Array.Empty<string>(),
            "Remove",
            "Low",
            p => p.Books == 0,
            p => "There are no books on this server, so this provider has nothing to do.")
    };
}

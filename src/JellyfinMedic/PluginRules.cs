using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>What the plugin rules need to know about the server.</summary>
public sealed class PluginContext
{
    public List<LibraryFacts> Libraries { get; set; } = new();

    public int CpuThreads { get; set; } = Environment.ProcessorCount;

    public bool LiveTvConfigured { get; set; }

    public long TotalItems => Libraries.Sum(l => l.ItemCount ?? 0);

    // Films and TV only: the items TMDb supplies artwork for (music, books and photos come from elsewhere).
    public long VideoItems => Libraries
        .Where(l => l.CollectionType is null or "" || l.CollectionType.Equals("movies", StringComparison.OrdinalIgnoreCase)
            || l.CollectionType.Equals("tvshows", StringComparison.OrdinalIgnoreCase) || l.CollectionType.Equals("mixed", StringComparison.OrdinalIgnoreCase)
            || l.CollectionType.Equals("boxsets", StringComparison.OrdinalIgnoreCase))
        .Where(l => !l.IsStreamed)
        .Sum(l => l.ItemCount ?? 0);

    public bool LargeLibrary => TotalItems >= 20_000;

    public List<LibraryFacts> Streamed => Libraries.Where(l => l.IsStreamed).ToList();

    public bool HasType(string type) => Libraries.Any(l => string.Equals(l.CollectionType, type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Rules for specific plugins, written from real settings files. Each rule reads the plugin's own
/// settings and only fires for plugins that are installed and active.
/// </summary>
public static class PluginRules
{
    private const string Area = "Plugins";

    public static void Apply(PluginReport report, string configFile, XElement root, IReadOnlyDictionary<string, XElement> active, PluginContext ctx)
    {
        switch (configFile.ToLowerInvariant())
        {
            case "jellyfin.xtream.library.xml": Xtream(report, root, ctx); break;
            case "introskipper.xml": IntroSkipper(report, root, ctx); break;
            case "jellyfin.plugin.homescreensections.xml": HomeScreenSections(report, root, active, ctx); break;
            case "jellyfin.plugin.javascriptinjector.xml": JavaScriptInjector(report, root, active, ctx); break;
            case "jellyfin.plugin.jellyfinenhanced.xml": JellyfinEnhanced(report, root, ctx); break;
            case "jellyfin.plugin.jellytweaks.xml": JellyTweaks(report, root, ctx); break;
            case "jellyfin.plugin.jellytag.xml":
            case "jellyfin.plugin.jellytagplus.xml": JellyTag(report, root, ctx); break;
            case "jellyfin.plugin.smartlists.xml": SmartLists(report, root, ctx); break;
            case "jellyfin.plugin.telegramnotifier.xml": TelegramNotifier(report, root, ctx); break;
            case "jellyfin.plugin.webhook.xml": Webhook(report, root, active, ctx); break;
            case "trakt.xml": Trakt(report, root, ctx); break;
            case "jellyfin.plugin.tmdb.xml": Tmdb(report, root, ctx); break;
            case "jellyfin.plugin.mediabar.xml": MediaBar(report, root, ctx); break;
            case "jellyfin.plugin.autocollections.xml": AutoCollections(report, root); break;
        }
    }

    // ---------- Xtream Library ----------

    private static void Xtream(PluginReport r, XElement root, PluginContext ctx)
    {
        var movieLib = ctx.Streamed.FirstOrDefault(l => string.Equals(l.CollectionType, "movies", StringComparison.OrdinalIgnoreCase));
        var showLib = ctx.Streamed.FirstOrDefault(l => string.Equals(l.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase));

        foreach (var p in Children(Child(root, "Providers"), "ProviderConfig").Where(p => Flag(p, "IsEnabled") != false))
        {
            string name = Text(p, "Name") ?? "provider";
            int vod = Strings(p, "SelectedVodCategoryIds").Count;
            int series = Strings(p, "SelectedSeriesCategoryIds").Count;

            if (Is(Text(p, "MovieCategoriesMode"), "Exclude") && Flag(p, "SyncMovies") != false)
            {
                Add(r, movieLib?.ItemCount >= 10_000 ? Sev.Improve : Sev.Tip,
                    $"{r.Name} imports every film category except the {vod} you've ticked ({name})",
                    $"Film categories: Exclude mode, {vod} ticked" + Count(movieLib),
                    "If you meant to import only those categories, switch film categories to Include",
                    "In Exclude mode the ticked categories are the ones left out, so the rest of your provider's film catalogue is still imported. A huge IPTV film library slows down every list, search and home screen row.");
            }

            if (Is(Text(p, "SeriesCategoriesMode"), "Include") && series == 0 && Flag(p, "SyncSeries") != false)
            {
                Add(r, Sev.Tip, $"{r.Name} has no series categories ticked ({name})",
                    "Series categories: Include mode, none ticked" + Count(showLib),
                    "Tick the series categories you want",
                    "With Include mode and nothing ticked, either no series are imported or the plugin treats it as everything. The item count of your IPTV shows library tells you which.");
            }

            if (Flag(p, "CleanupOrphans") == true && Number(p, "OrphanSafetyThreshold") is { } threshold && threshold < 0.5)
            {
                Add(r, Sev.Tip, $"{r.Name} may not remove items from categories you've dropped ({name})",
                    $"Orphan safety threshold: {threshold:P0}",
                    "If the IPTV item counts don't drop after the next sync, raise it for one sync, then set it back",
                    "This safety limit stops one sync deleting more than that share of the library, which protects you if the provider has an outage. After cutting a lot of categories it can also block the clean-up you want.");
            }

            if (Number(p, "SyncParallelism") is { } parallel && parallel > 4)
            {
                Add(r, Sev.Tip, $"{r.Name} opens {parallel:0} connections at once to your provider ({name})",
                    $"{parallel:0} at once", "2 to 4",
                    "Many IPTV providers only allow a few connections per account. Going over can get syncs rate-limited or the account temporarily blocked.");
            }
        }

        if (Is(Text(root, "SyncScheduleType"), "Interval") && Number(root, "SyncIntervalMinutes") is { } minutes && minutes < 360)
        {
            Add(r, Sev.Tip, $"{r.Name} syncs every {minutes:0} minutes", $"Every {minutes:0} minutes", "Once a day, overnight",
                "Each sync can add, change or remove thousands of items, which sets off library scans and every plugin that reacts to new items.");
        }

        if (Flag(root, "EnableEpg") == true && Number(root, "EpgDaysToFetch") is { } days && days > 3)
        {
            Add(r, Sev.Tip, $"{r.Name} downloads {days:0} days of TV guide", $"{days:0} days", "1 to 3 days",
                "The guide is downloaded for every channel; extra days make each refresh slower and the database bigger.");
        }
    }

    // ---------- Intro Skipper ----------

    private static void IntroSkipper(PluginReport r, XElement root, PluginContext ctx)
    {
        var excluded = Strings(root, "PathExclusions");
        var missing = ctx.Streamed
            .Where(l => l.Locations.Any(loc => !excluded.Any(ex => PathCovers(ex, loc))))
            .ToList();
        if (missing.Count > 0)
        {
            Add(r, Sev.Improve, $"{r.Name} analyses your IPTV libraries",
                "Not excluded: " + string.Join(", ", missing.Select(l => l.Name)),
                "Add their folders to Path exclusions: " + string.Join(", ", missing.SelectMany(l => l.Locations)),
                "Analysing streamed episodes means pulling each one from your provider. That's slow, and can hit your provider's connection limits. SkipMe.db can supply skip times for those libraries without any analysis.");
        }

        if (Flag(root, "AutoDetectIntros") == true)
        {
            Add(r, Sev.Tip, $"{r.Name} analyses new episodes during library scans", "On", "Off: let its scheduled task do it overnight",
                "Analysis during scans makes every scan longer and runs at whatever time the scan happens.");
        }

        if (Number(root, "MaxParallelism") is { } parallel && parallel > Math.Max(1, ctx.CpuThreads / 2))
        {
            Add(r, Sev.Improve, $"{r.Name} analyses {parallel:0} episodes at once", $"{parallel:0}", $"No more than {Math.Max(1, ctx.CpuThreads / 4)}",
                "Each analysis runs FFmpeg. Too many at once competes with playback for CPU.");
        }

        string? priority = Text(root, "ProcessPriority");
        if (priority is not null && !Is(priority, "BelowNormal") && !Is(priority, "Idle") && !Is(priority, "Lowest"))
        {
            Add(r, Sev.Tip, $"{r.Name} runs analysis at {priority} priority", priority, "BelowNormal",
                "At normal priority, analysis competes equally with playback. BelowNormal lets playback go first.");
        }
    }

    // ---------- Home Screen Sections ----------

    private static void HomeScreenSections(PluginReport r, XElement root, IReadOnlyDictionary<string, XElement> active, PluginContext ctx)
    {
        if (Flag(root, "Enabled") == false)
        {
            return;
        }

        var enabled = Children(Child(root, "SectionSettings"), "SectionSettings")
            .Where(s => Flag(s, "Enabled") != false)
            .GroupBy(s => Text(s, "SectionId") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Number(g.First(), "UpperLimit") ?? 1, StringComparer.OrdinalIgnoreCase);

        bool seerr = !string.IsNullOrWhiteSpace(Text(root, "JellyseerrUrl"));
        var needs = new (string Section, bool Ok, string Missing)[]
        {
            ("Discover", seerr, "Seerr"), ("DiscoverMovies", seerr, "Seerr"), ("DiscoverTV", seerr, "Seerr"), ("MyJellyseerrRequests", seerr, "Seerr"),
            ("UpcomingShows", HasText(root, "Sonarr", "Url"), "Sonarr"), ("UpcomingMovies", HasText(root, "Radarr", "Url"), "Radarr"),
            ("UpcomingMusic", HasText(root, "Lidarr", "Url"), "Lidarr"), ("UpcomingBooks", HasText(root, "Readarr", "Url"), "Readarr")
        };
        var broken = needs.Where(n => enabled.ContainsKey(n.Section) && !n.Ok).ToList();
        if (broken.Count > 0)
        {
            string missing = string.Join(", ", broken.Select(b => b.Missing).Distinct());
            bool enhancedHasSeerr = active.TryGetValue("Jellyfin.Plugin.JellyfinEnhanced.xml", out var je) && !string.IsNullOrWhiteSpace(Text(je, "JellyseerrUrls"));
            Add(r, Sev.Improve, $"{r.Name} has {broken.Count} sections that can't work",
                string.Join(", ", broken.Select(b => Friendly(b.Section))) + $" (no {missing} address in its settings)",
                $"Turn those sections off, or fill in the {missing} address in {r.Name}'s settings",
                "Without an address these rows come back empty, but they're still requested every time someone opens the home page."
                + (enhancedHasSeerr && broken.Any(b => b.Missing == "Seerr") ? " Jellyfin Enhanced has a Seerr address, but Home Screen Sections keeps its own." : string.Empty));
        }

        var forContent = new (string Section, bool Have)[]
        {
            ("RecentlyAddedAlbums", ctx.HasType("music")), ("RecentlyAddedArtists", ctx.HasType("music")), ("LatestAlbums", ctx.HasType("music")),
            ("RecentlyAddedBooks", ctx.HasType("books")), ("RecentlyAddedAudioBooks", ctx.HasType("books")),
            ("LatestBooks", ctx.HasType("books")), ("LatestAudioBooks", ctx.HasType("books")),
            ("RecentlyAddedMusicVideos", ctx.HasType("musicvideos")), ("LatestMusicVideo", ctx.HasType("musicvideos")),
            ("LiveTV", ctx.LiveTvConfigured)
        };
        var unused = forContent.Where(c => enabled.ContainsKey(c.Section) && !c.Have).ToList();
        if (unused.Count > 0)
        {
            Add(r, Sev.Tip, $"{r.Name} has {unused.Count} sections for content you don't have",
                string.Join(", ", unused.Select(u => Friendly(u.Section))), "Turn them off",
                "Each enabled section is a separate request when the home page loads, even when it comes back empty.");
        }

        if (ctx.LargeLibrary)
        {
            var heavy = new List<string>();
            if (enabled.TryGetValue("Genre", out var g) && g > 1) heavy.Add($"Genre (up to {g:0} rows)");
            if (enabled.TryGetValue("BecauseYouWatched", out var b) && b > 1) heavy.Add($"Because You Watched (up to {b:0} rows)");
            if (enabled.TryGetValue("RecentlyAddedInLibrary", out var l) && l > 3) heavy.Add("Recently Added in each library");
            if (heavy.Count > 0)
            {
                Add(r, Sev.Tip, $"{r.Name} builds several rows that search your whole library",
                    string.Join(", ", heavy),
                    "Lower their row limits, and hide your IPTV libraries from the home screen",
                    $"With {ctx.TotalItems:N0} items, each of these rows is a heavy query every time the home page opens. Home Screen Sections skips libraries hidden from the home screen.");
            }
        }
    }

    // ---------- JavaScript Injector (and KefinTweaks, which lives inside it) ----------

    private static void JavaScriptInjector(PluginReport r, XElement root, IReadOnlyDictionary<string, XElement> active, PluginContext ctx)
    {
        var scripts = Children(Child(root, "CustomJavaScripts"), "CustomJavaScriptEntry")
            .Select(e => (Name: Text(e, "Name") ?? "unnamed", Script: Text(e, "Script") ?? string.Empty, Enabled: Flag(e, "Enabled") != false))
            .ToList();

        foreach (var old in scripts.Where(s => s.Enabled && s.Script.Contains("kefintweaks-loader", StringComparison.OrdinalIgnoreCase)))
        {
            AddRaw(r, Sev.Improve, $"JavaScript Injector has an old KefinTweaks loader (\"{old.Name}\")", "Loads kefintweaks-loader.js",
                "Delete this script; KefinTweaks now loads through kefinTweaks-plugin.js",
                "The old loader file no longer exists, so every page load makes a request that fails.", "Dashboard → Plugins → JavaScript Injector");
        }

        var loaders = scripts.Where(s => s.Enabled && s.Script.Contains("kefinTweaks-plugin.js", StringComparison.OrdinalIgnoreCase)).ToList();
        if (loaders.Count > 1)
        {
            AddRaw(r, Sev.Tip, "KefinTweaks is loaded more than once", string.Join(", ", loaders.Select(l => $"\"{l.Name}\"")),
                "Keep one KefinTweaks script and delete the others", "Each copy runs its own home screen and features, doubling the work.",
                "Dashboard → Plugins → JavaScript Injector");
        }

        var config = scripts.FirstOrDefault(s => s.Enabled && s.Name.Equals("KefinTweaks-Config", StringComparison.OrdinalIgnoreCase));
        if (loaders.Count == 0 || config.Script is null)
        {
            return;
        }

        using var json = ParseKefinConfig(config.Script);
        if (json is null)
        {
            return;
        }

        var cfg = json.RootElement;
        if (JsonBool(cfg, "scripts", "homeScreen") != true)
        {
            return;
        }

        var heavy = new List<string>();
        if (JsonBool(cfg, "homeScreen", "imdbTop250", "enabled") == true) heavy.Add("IMDb Top 250 (matches every film in your library against IMDb's list)");
        if (JsonBool(cfg, "homeScreen", "discovery", "enabled") == true)
        {
            int types = JsonCountEnabled(cfg, "homeScreen", "discovery", "sectionTypes");
            bool infinite = JsonBool(cfg, "homeScreen", "discovery", "infiniteScroll") == true;
            heavy.Add($"Discovery ({types} row types{(infinite ? ", loading more as you scroll" : string.Empty)})");
        }

        if (JsonBool(cfg, "homeScreen", "seasonal", "enabled") == true) heavy.Add("Seasonal rows (genre rows such as Halloween's Horror and Thriller)");
        if (JsonBool(cfg, "homeScreen", "trending", "enabled") == true) heavy.Add("Trending");
        if (JsonBool(cfg, "homeScreen", "popularTVNetworks", "enabled") == true) heavy.Add("Popular TV Networks");
        bool random = string.Equals(JsonText(cfg, "homeScreen", "defaultSortOrder"), "Random", StringComparison.OrdinalIgnoreCase);

        AddRaw(r, ctx.LargeLibrary ? Sev.Improve : Sev.Tip, "KefinTweaks' home screen is on",
            heavy.Count > 0 ? string.Join("; ", heavy) : "Home screen enabled",
            "Turn off Home Screen in KefinTweaks' settings, or at least IMDb Top 250, Discovery and Seasonal",
            (ctx.LargeLibrary ? $"With {ctx.TotalItems:N0} items, these rows ask Jellyfin for huge lists every time someone opens the home page, and the requests pile up faster than they finish. " : "These rows ask Jellyfin for long lists every time someone opens the home page. ")
            + (random ? "Most are set to random order, which is the slowest kind of list." : string.Empty),
            "Dashboard → Plugins → KefinTweaks → Configure KefinTweaks");

        if (active.TryGetValue("Jellyfin.Plugin.HomeScreenSections.xml", out var hss) && Flag(hss, "Enabled") != false)
        {
            AddRaw(r, Sev.Tip, "KefinTweaks and Home Screen Sections both build the home screen", "Both on",
                "Use one of them for the home screen",
                "Each builds its own set of rows, so the home page makes both sets of requests.", "Dashboard → Plugins");
        }
    }

    // ---------- Jellyfin Enhanced ----------

    private static void JellyfinEnhanced(PluginReport r, XElement root, PluginContext ctx)
    {
        bool seerr = !string.IsNullOrWhiteSpace(Text(root, "JellyseerrUrls"));
        if (seerr && Flag(root, "TriggerSeerrScanOnItemAdded") == true)
        {
            double debounce = Number(root, "SeerrScanDebounceSeconds") ?? 0;
            Add(r, ctx.Streamed.Count > 0 || ctx.LargeLibrary ? Sev.Improve : Sev.Tip,
                $"{r.Name} asks Seerr to rescan whenever something is added",
                $"On, at most every {debounce:0} seconds",
                "Off (Seerr's own scheduled scan is enough), or at least 1800 seconds",
                "Every time items are added, Seerr is told to rescan, and it then reads through your Jellyfin libraries. When IPTV syncs keep adding items, that happens over and over. Also untick your IPTV libraries in Seerr's own Jellyfin settings.");
        }
    }

    // ---------- Jellyfin Tweaks ----------

    private static void JellyTweaks(PluginReport r, XElement root, PluginContext ctx)
    {
        var streamed = ctx.Streamed.Where(l => l.Id.Length > 0).ToList();
        if (streamed.Count > 0)
        {
            var excluded = Strings(root, "LatestItemsExcludes").Select(NormaliseId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notHidden = streamed.Where(l => Flag(root, "ManageLatestItemsExcludes") != true || !excluded.Contains(l.Id)).ToList();
            if (notHidden.Count > 0)
            {
                Add(r, Sev.Tip, $"{r.Name} can hide your IPTV libraries from everyone's home screen",
                    "Shown on home screens: " + string.Join(", ", notHidden.Select(l => l.Name)),
                    "Turn on Manage latest items excludes and tick: " + string.Join(", ", notHidden.Select(l => l.Name)),
                    "Home screen rows then skip the IPTV libraries for every user at once, and Home Screen Sections respects the same choice. Those rows get much lighter.");
            }
        }

        if (ctx.LargeLibrary && Number(root, "MaxDaysNextUp") is 0)
        {
            Add(r, Sev.Tip, $"{r.Name} lets Next Up look through all viewing history", "No limit", "30–60 days",
                "On a large library, Next Up checks every series ever watched each time the home page opens.");
        }
    }

    // ---------- JellyTag / JellyTag Plus ----------

    private static void JellyTag(PluginReport r, XElement root, PluginContext ctx)
    {
        if (Flag(root, "Enabled") == false)
        {
            return;
        }

        var excluded = Strings(root, "ExcludedLibraryIds").Select(NormaliseId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagged = ctx.Streamed.Where(l => l.Id.Length > 0 && !excluded.Contains(l.Id)).ToList();
        if (tagged.Count > 0)
        {
            Add(r, Sev.Improve, $"{r.Name} draws badges on your IPTV posters",
                "Not excluded: " + string.Join(", ", tagged.Select(l => l.Name)),
                "Exclude: " + string.Join(", ", tagged.Select(l => l.Name)),
                "Every poster it badges is decoded, drawn on and re-saved. Across a large IPTV catalogue that's a lot of CPU and cache space for little benefit.");
        }

        if (Number(root, "CacheDurationHours") is { } hours && hours > 0 && hours < 72)
        {
            Add(r, Sev.Tip, $"{r.Name} redraws its badges every {hours:0} hours", $"{hours:0} hours", "168 hours (a week)",
                "When the cache expires, every badged image is drawn again the next time it's shown.");
        }

        if (Child(root, "WarmerMaxConcurrency") is not null && Flag(root, "WarmerPauseDuringPlayback") == false)
        {
            Add(r, Sev.Tip, $"{r.Name} pre-draws badges while people are watching", "Off", "Pause during playback: On",
                "Its background warmer keeps drawing images during playback, competing with transcoding for CPU.");
        }
    }

    // ---------- SmartLists ----------

    private static void SmartLists(PluginReport r, XElement root, PluginContext ctx)
    {
        if (Is(Text(root, "DefaultAutoRefresh"), "OnLibraryChanges") && (ctx.Streamed.Count > 0 || ctx.LargeLibrary))
        {
            Add(r, Sev.Tip, $"{r.Name} rebuilds lists on every library change", "New lists: refresh on library changes",
                "Use a daily schedule instead, and check each existing list's refresh setting",
                "IPTV syncs and scans change the library constantly, so lists set to refresh on changes are rebuilt over and over.");
        }
    }

    // ---------- Telegram Notifier ----------

    private static void TelegramNotifier(PluginReport r, XElement root, PluginContext ctx)
    {
        if (Flag(root, "EnablePlugin") == false || ctx.Streamed.Count == 0)
        {
            return;
        }

        bool itemAdded = Children(Child(root, "UserConfigurations"), "UserConfiguration")
            .Any(u => Flag(u, "EnableUser") != false && Flag(u, "ItemAdded") == true &&
                      (Flag(u, "ItemAddedMovies") == true || Flag(u, "ItemAddedEpisodes") == true));
        if (itemAdded)
        {
            Add(r, Sev.Tip, $"{r.Name} sends a message for every item added, IPTV imports included", "Item added: on for films/episodes",
                "Turn off item-added messages for episodes (and films if needed)",
                "An IPTV sync can add hundreds of items at once, which means hundreds of Telegram messages.");
        }
    }

    // ---------- Webhook ----------

    private static void Webhook(PluginReport r, XElement root, IReadOnlyDictionary<string, XElement> active, PluginContext ctx)
    {
        var destinations = root.Elements()
            .Where(e => e.Name.LocalName.EndsWith("Options", StringComparison.Ordinal))
            .SelectMany(e => e.Elements())
            .Where(e => Flag(e, "EnableWebhook") != false)
            .ToList();

        int empty = destinations.Count(d => string.IsNullOrWhiteSpace(Text(d, "WebhookUri")));
        if (empty > 0)
        {
            Add(r, Sev.Tip, $"{r.Name} has {(empty == 1 ? "an empty destination" : $"{empty} empty destinations")}", "No address", "Delete it",
                "It's switched on but has nowhere to send to.");
        }

        foreach (var d in destinations)
        {
            string uri = Text(d, "WebhookUri") ?? string.Empty;
            string name = Text(d, "WebhookName") is { Length: > 0 } n ? n : "unnamed";
            if (uri.Length > 0 && (uri.Trim().Contains(' ', StringComparison.Ordinal) || uri.Contains('`', StringComparison.Ordinal) || uri.Contains("[span", StringComparison.OrdinalIgnoreCase)))
            {
                // The address isn't shown: it can contain a bot token.
                Add(r, Sev.Problem, $"{r.Name}'s \"{name}\" address has extra text on the end", "Text after the web address",
                    "Re-enter the address cleanly, with nothing after it",
                    "Something was pasted after the address (spaces, backticks or other text), so every notification to it fails.");
            }

            bool itemAdded = d.Descendants().Any(x => x.Name.LocalName == "NotificationType" && Is(x.Value, "ItemAdded"));
            if (itemAdded && ctx.Streamed.Count > 0)
            {
                bool telegram = uri.Contains("api.telegram.org", StringComparison.OrdinalIgnoreCase);
                bool notifier = active.ContainsKey("Jellyfin.Plugin.TelegramNotifier.xml");
                Add(r, Sev.Tip, $"{r.Name} \"{name}\" sends a message for every item added",
                    "Item added: on" + (telegram && notifier ? "; Telegram Notifier also does this" : string.Empty),
                    telegram && notifier ? "Use one of them for new-item messages" : "Turn off Item added, or limit it to films",
                    "IPTV syncs add items by the hundred, so you'd get a flood of messages" + (telegram && notifier ? ", twice." : "."));
            }
        }
    }

    // ---------- Trakt ----------

    private static void Trakt(PluginReport r, XElement root, PluginContext ctx)
    {
        var libraries = ctx.Libraries.Where(l => !string.Equals(l.CollectionType, "boxsets", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var user in Children(Child(root, "TraktUsers"), "TraktUser"))
        {
            var excluded = Strings(user, "LocationsExcluded");
            bool Covered(LibraryFacts l) => l.Locations.Count > 0 && l.Locations.All(loc => excluded.Any(ex => PathCovers(ex, loc)));

            if (libraries.Count > 0 && libraries.All(Covered))
            {
                Add(r, Sev.Tip, $"{r.Name} excludes every library folder", "All library folders excluded",
                    "Remove your local Movies and TV folders from the exclusions if you want them synced",
                    "With everything excluded, nothing is synced to your Trakt collection, and watches may not be scrobbled either.");
            }
            else
            {
                var iptv = ctx.Streamed.Where(l => !Covered(l)).ToList();
                if (iptv.Count > 0)
                {
                    Add(r, Sev.Improve, $"{r.Name} syncs your IPTV libraries to Trakt",
                        "Not excluded: " + string.Join(", ", iptv.Select(l => l.Name)),
                        "Exclude their folders: " + string.Join(", ", iptv.SelectMany(l => l.Locations)),
                        "Your Trakt collection would fill with the whole IPTV catalogue, and every sync works through all of it.");
                }
            }
        }
    }

    // ---------- TMDb ----------

    private static void Tmdb(PluginReport r, XElement root, PluginContext ctx)
    {
        if (ctx.VideoItems < 5_000)
        {
            return;
        }

        var original = new[] { "PosterSize", "BackdropSize", "StillSize", "LogoSize", "ProfileSize" }
            .Where(k => Is(Text(root, k), "original"))
            .ToList();
        if (original.Count > 0)
        {
            Add(r, Sev.Tip, $"{r.Name} downloads full-size original images",
                string.Join(", ", original) + ": original",
                "w500 for posters, w1280 for backdrops",
                $"Originals are often several megabytes each. Across {ctx.VideoItems:N0} films and TV items that's a lot of disk space, and slower metadata refreshes.");
        }
    }

    // ---------- Media Bar ----------

    private static void MediaBar(PluginReport r, XElement root, PluginContext ctx)
    {
        string? enabled = Text(root, "Enabled");
        if (ctx.LargeLibrary && enabled is not null && !Is(enabled, "Disabled") && !Is(enabled, "false"))
        {
            Add(r, Sev.Tip, $"{r.Name} picks random items from your whole library", "On",
                "Limit it to your local libraries or a collection if its settings allow",
                $"Random picks across {ctx.TotalItems:N0} items are slow, and they hold up the rest of the home page while they load.");
        }
    }

    // ---------- Auto Collections ----------

    private static void AutoCollections(PluginReport r, XElement root)
    {
        var titles = Children(Child(root, "TitleMatchPairs"), "TitleMatchPair").Select(t => Text(t, "TitleMatch") ?? string.Empty).ToList();
        string[] examples = { "Marvel", "Star Wars", "Harry Potter", "Lord of the Rings", "Pirates", "Jurassic" };
        if (examples.All(e => titles.Contains(e, StringComparer.OrdinalIgnoreCase)))
        {
            Add(r, Sev.Tip, $"{r.Name} still has its example collections", string.Join(", ", examples),
                "Keep the ones you want and delete the rest",
                "Each collection is rebuilt by searching your whole library, IPTV included, whenever its task runs.");
        }
    }

    // ---------- Helpers ----------

    private static void Add(PluginReport r, string severity, string title, string current, string recommended, string why) =>
        AddRaw(r, severity, title, current, recommended, why, $"Dashboard → Plugins → {r.Name}");

    private static void AddRaw(PluginReport r, string severity, string title, string current, string recommended, string why, string where) =>
        r.Findings.Add(new Finding
        {
            Area = Area,
            Severity = severity,
            Title = title,
            Current = current,
            Recommended = recommended,
            Why = why,
            Where = where
        });

    private static string Count(LibraryFacts? lib) =>
        lib?.ItemCount is { } n ? $"; {lib.Name} has {n.ToString("N0", CultureInfo.InvariantCulture)} items" : string.Empty;

    private static XElement? Child(XElement? e, string name) =>
        e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? e, string name) =>
        e?.Elements().Where(x => x.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    private static string? Text(XElement? e, params string[] path)
    {
        XElement? current = e;
        foreach (var part in path)
        {
            current = Child(current, part);
        }

        return current?.Value;
    }

    private static bool HasText(XElement? e, params string[] path) => !string.IsNullOrWhiteSpace(Text(e, path));

    private static bool? Flag(XElement? e, params string[] path) =>
        bool.TryParse(Text(e, path)?.Trim(), out var b) ? b : null;

    private static double? Number(XElement? e, params string[] path) =>
        double.TryParse(Text(e, path)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static List<string> Strings(XElement? e, string name) =>
        Child(e, name)?.Elements().Select(x => x.Value.Trim()).Where(v => v.Length > 0).ToList() ?? new List<string>();

    private static bool Is(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string NormaliseId(string id) =>
        Guid.TryParse(id, out var g) ? g.ToString("N") : id.Trim().ToLowerInvariant();

    private static bool PathCovers(string excluded, string location)
    {
        string ex = excluded.Trim().TrimEnd('/', '\\');
        string loc = location.Trim().TrimEnd('/', '\\');
        return ex.Length > 0 && (loc.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                                 loc.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase) ||
                                 loc.StartsWith(ex + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"RecentlyAddedAudioBooks" becomes "Recently Added Audio Books".</summary>
    private static string Friendly(string id) => Regex.Replace(id, "(?<=[a-z])(?=[A-Z])", " ");

    // ---------- KefinTweaks configuration (JSON inside a JavaScript Injector script) ----------

    private static JsonDocument? ParseKefinConfig(string script)
    {
        int marker = script.IndexOf("KefinTweaksConfig", StringComparison.Ordinal);
        int start = marker < 0 ? -1 : script.IndexOf('{', marker);
        int end = script.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(script[start..(end + 1)], new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? JsonAt(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static bool? JsonBool(JsonElement root, params string[] path) => JsonAt(root, path) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null
    };

    private static string? JsonText(JsonElement root, params string[] path) =>
        JsonAt(root, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static int JsonCountEnabled(JsonElement root, params string[] path)
    {
        if (JsonAt(root, path) is not { ValueKind: JsonValueKind.Object } obj)
        {
            return 0;
        }

        return obj.EnumerateObject().Count(p =>
            p.Value.ValueKind == JsonValueKind.Object &&
            (!p.Value.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False));
    }
}

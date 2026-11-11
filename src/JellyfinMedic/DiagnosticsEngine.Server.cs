using System.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>Checks: themes, serious errors, resource spikes, general performance and server settings.</summary>
public sealed partial class DiagnosticsEngine
{
    // ---------- Themes (custom CSS) ----------

    private string CustomCss() => SettingsReader.Text(TryConfig("branding"), "CustomCss") ?? string.Empty;

    private static readonly System.Text.RegularExpressions.Regex ImportRule = new(
        @"@import\s+(?:url\(\s*)?[""']?([^""')\s;]+)[""']?\s*\)?[^;]*;?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex Comments = new(@"/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>Each @import in the CSS, with whether it comes after other rules (browsers ignore those).</summary>
    public static List<(string Url, bool Late)> ThemeImports(string css)
    {
        string text = Comments.Replace(css ?? string.Empty, string.Empty);
        var found = new List<(string, bool)>();
        foreach (System.Text.RegularExpressions.Match m in ImportRule.Matches(text))
        {
            // Only @charset and other @imports may come before an @import.
            string before = ImportRule.Replace(text[..m.Index], string.Empty);
            before = System.Text.RegularExpressions.Regex.Replace(before, @"@charset[^;]*;", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            found.Add((m.Groups[1].Value.Trim(), before.Trim().Length > 0));
        }

        return found;
    }

    private void CheckTheme()
    {
        string css = CustomCss();
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        var imports = ThemeImports(css);
        foreach (var (url, late) in imports)
        {
            if (late)
            {
                Add(AreaTheme, Sev.Improve, "A theme import in your custom CSS is ignored", url,
                    "Move every @import line to the very top of the custom CSS",
                    "Browsers skip an @import that comes after any other rule, so this theme never loads. It's the most common reason a theme \"stops working\" after adding a tweak above it.",
                    WhereCustomCss);
            }

            if (url.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                Add(AreaTheme, Sev.Improve, "A theme is loaded from raw.githubusercontent.com", url,
                    "Use the theme's jsDelivr or GitHub Pages address from its install page instead",
                    "GitHub serves these files as plain text and tells browsers not to guess, so browsers refuse to use them as a stylesheet and the theme doesn't apply.",
                    WhereCustomCss);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                Add(AreaTheme, Sev.Tip, "A theme is loaded over plain http", url, "The https:// address",
                    "When you open Jellyfin over https (for example through a reverse proxy), browsers block http stylesheets, so the theme only works on some devices.",
                    WhereCustomCss);
            }
        }

        string code = Comments.Replace(css, string.Empty);
        int open = code.Count(c => c == '{');
        int close = code.Count(c => c == '}');
        if (open != close)
        {
            Add(AreaTheme, Sev.Improve, "Your custom CSS has unbalanced braces", $"{open} opening {{ and {close} closing }}",
                "Find the rule missing a brace (usually the last one you added)",
                "Browsers drop everything from the broken rule onwards, so parts of your theme or tweaks silently stop applying, and pages can draw oddly.",
                WhereCustomCss);
        }

        if (css.Contains("/*", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.Matches(css, @"/\*").Count > System.Text.RegularExpressions.Regex.Matches(css, @"\*/").Count)
        {
            Add(AreaTheme, Sev.Improve, "A comment in your custom CSS is never closed", "/* without */",
                "Close the comment with */",
                "Everything after an unclosed comment is ignored, including any theme or tweaks below it.",
                WhereCustomCss);
        }
    }

    // ---------- Serious errors (also shown as home-page banners) ----------

    private void CheckCriticalErrors()
    {
        foreach (var alert in AdminAlerts.CriticalFromLogs(_paths.LogDirectoryPath))
        {
            int split = alert.Detail.IndexOf(" Last seen: ", StringComparison.Ordinal);
            string advice = split > 0 ? alert.Detail[..split] : alert.Detail;
            string seen = split > 0 ? alert.Detail[(split + 12)..] : string.Empty;
            Add("Logs", Sev.Problem, alert.Title, seen, advice,
                "Jellyfin logged this as an error. It's shown however rarely it happens, as it can mean data loss or a server that stops working.",
                "Dashboard → Logs");
        }
    }

    // ---------- Resource spikes ----------

    private void CheckResourceSpikes()
    {
        var recent = ResourceLog.Load(_paths).Where(e => e.TimeUtc > DateTime.UtcNow.AddDays(-7)).ToList();
        if (recent.Count == 0)
        {
            return;
        }

        // The causes seen most often, without their percentages, so the same task counts once.
        var top = recent.SelectMany(e => e.Causes)
            .Select(c => System.Text.RegularExpressions.Regex.Replace(c, @" \(\d+%\)$", string.Empty))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()}×)");
        var kinds = recent.GroupBy(e => e.Resource).Select(g => $"{g.Key} {g.Count()}×");
        Add(AreaServer, recent.Count >= 10 ? Sev.Improve : Sev.Tip, $"Jellyfin ran flat out {recent.Count} time{(recent.Count == 1 ? string.Empty : "s")} this week",
            string.Join(", ", kinds) + ". Most often running: " + string.Join("; ", top),
            "Move the tasks named here to quieter times (Schedule), or let the GPU take the transcodes",
            "Each time, Jellyfin and its FFmpeg processes used nearly all of a resource for at least 45 seconds, which is when playback buffers and pages load slowly. Medic → Dashboard lists each one and what was running.",
            "Medic → Dashboard", id: "server.flat-out");
    }

    // ---------- General performance ----------

    private void CheckPerformance(object server, HardwareInfo hw, int pluginCount)
    {
        if (SystemProbe.Spinning(_paths.DataPath) == true)
        {
            Add(AreaServer, Sev.Improve, "Jellyfin's database is on a spinning hard drive", HostPlatform.DataFolder + " is on a hard drive",
                "Move it to an SSD (on Unraid, the cache pool)",
                "The database does thousands of small reads and writes. On a hard drive every page of the web UI, every scan and every \"continue watching\" waits for the disk. An SSD makes Jellyfin feel several times quicker.",
                WhereDocker);
        }

        long db = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db"));
        if (db > 4L * 1024 * 1024 * 1024)
        {
            Add(AreaServer, Sev.Tip, "The database is very large", SystemProbe.Size(db),
                "Keep the activity log for 30–90 days, remove libraries you no longer use, and let \"Optimize database\" run weekly",
                "A big database makes scans, searches and the home screen slower. Old activity entries and libraries of channels nobody watches are the usual causes.",
                WhereTasks);
        }

        if (StorageWatch.FolderSize(_paths.ImageCachePath) is { } images && images.Bytes > 30L * 1024 * 1024 * 1024)
        {
            Add(AreaServer, Sev.Tip, "The image cache is very large", SystemProbe.Size(images.Bytes) + (images.Complete ? string.Empty : " or more"),
                "Run \"Clean Cache Directory\", and keep the cache on an SSD",
                "Resized artwork piles up here. It's rebuilt as needed, so clearing it is safe, and on a slow disk a huge cache makes artwork load slowly.",
                WhereTasks);
        }

        long? imageLimit = SettingsReader.Number(server, "ParallelImageEncodingLimit");
        if (imageLimit is 0 && hw.CpuThreads is > 0 and <= 4)
        {
            Add(AreaServer, Sev.Tip, "Image resizing can use every CPU thread", "Unlimited", "2",
                $"With {hw.CpuThreads} CPU threads, a page full of new artwork can make playback stutter while images are resized. A limit of 2 keeps a thread free.",
                WhereGeneral);
        }

        if (pluginCount >= 30)
        {
            Add(AreaServer, Sev.Tip, "Lots of plugins are installed", pluginCount.ToString(CultureInfo.InvariantCulture), "Only the ones you use",
                "Each plugin loads at start-up and many run their own background work and scheduled tasks. Medic → Plugin directory lists ones you probably don't need any more.",
                "Dashboard → Plugins");
        }
    }

    // ---------- Server settings ----------

    private void CheckServer(object server, HardwareInfo hw, List<LibraryFacts> libraries)
    {
        long? bitrate = SettingsReader.Number(server, "RemoteClientBitrateLimit");
        if (bitrate > 0 && bitrate < 4_000_000)
        {
            Add(AreaServer, Sev.Improve, "Remote streaming is capped very low", $"{bitrate / 1_000_000d:0.#} Mbps",
                "At least 8 Mbps, or match your upload speed",
                "Below about 4 Mbps, almost everything watched away from home has to be transcoded, which loads the server and lowers quality.",
                "Dashboard → Playback → Streaming");
        }

        CheckBitrateAgainstSpeedTest(bitrate ?? 0);

        if (hw.RandomHostname && string.IsNullOrWhiteSpace(_config.Configuration.ServerName))
        {
            Add(AreaServer, Sev.Tip, "The server has no name", "(not set)", "A name such as \"Tower Jellyfin\"",
                "In a container an unnamed server shows the container's random ID in apps, which also changes if the container is recreated.",
                WhereGeneral);
        }

        if (SettingsReader.TryGet(server, "ActivityLogRetentionDays", out var retention) && (SettingsReader.ToNumber(retention) is null or <= 0))
        {
            Add(AreaServer, Sev.Tip, "The activity log is kept forever", "Forever", "30–90 days",
                "Every playback and login is recorded; over years this grows the database for no benefit.", WhereGeneral);
        }

        foreach (var (setting, label) in new[]
                 {
                     ("LibraryScanFanoutConcurrency", "Parallel library scan tasks"),
                     ("LibraryMetadataRefreshConcurrency", "Parallel metadata refresh tasks"),
                     ("ParallelImageEncodingLimit", "Parallel image encoding")
                 })
        {
            long? value = SettingsReader.Number(server, setting);
            if (value > hw.CpuThreads)
            {
                Add(AreaServer, Sev.Improve, $"{label} is set higher than your CPU threads", value.Value.ToString(CultureInfo.InvariantCulture),
                    "0 (automatic) or no more than " + hw.CpuThreads,
                    "More parallel work than threads makes everything slower and can lock up the web UI during scans.",
                    "Dashboard → General → Performance");
            }
        }

        long totalItems = libraries.Sum(l => l.ItemCount ?? 0);
        long? monitorDelay = SettingsReader.Number(server, "LibraryMonitorDelay");
        if (totalItems >= 10_000 && monitorDelay < 60)
        {
            Add(AreaServer, Sev.Tip, "Library changes are picked up very quickly", $"{monitorDelay} seconds", "120 seconds",
                "With a library your size, a short delay means bulk changes (like a playlist update) trigger many small scans instead of one.",
                "Dashboard → Libraries → Display or Advanced");
        }

        bool trickplayUsed = libraries.Any(l => SettingsReader.Bool(l.Options, "EnableTrickplayImageExtraction") == true);
        if (trickplayUsed)
        {
            bool gpu = hw.GpuVendor is "intel" or "amd" or "nvidia";
            if (gpu && SettingsReader.Bool(server, "TrickplayOptions.EnableHwAcceleration") == false)
            {
                Add(AreaServer, Sev.Improve, "Trickplay images are made on the CPU", "Hardware acceleration off", "On",
                    "Generating seek-bar previews is very heavy; your GPU can do it many times faster.",
                    "Dashboard → Playback → Trickplay");
            }

            long? tpThreads = SettingsReader.Number(server, "TrickplayOptions.ProcessThreads");
            if (tpThreads > hw.CpuThreads)
            {
                Add(AreaServer, Sev.Improve, "Trickplay is set to use more threads than you have", tpThreads.Value.ToString(CultureInfo.InvariantCulture),
                    $"No more than {Math.Max(1, hw.CpuThreads / 2)}", "It competes with playback for CPU time.",
                    "Dashboard → Playback → Trickplay");
            }
        }
    }

    /// <summary>
    /// Compares the remote streaming limit with the last internet speed test (Tests tab),
    /// assuming two people watching away from home at once.
    /// </summary>
    private void CheckBitrateAgainstSpeedTest(long currentBps)
    {
        var test = SpeedStore.Load(_paths);
        if (test is null || test.UploadMbps <= 0 || test.TestedUtc < DateTime.UtcNow.AddDays(-90))
        {
            return;
        }

        var option = SpeedAdvice.Options(test.UploadMbps).First(o => o.Viewers == 2);
        double currentMbps = currentBps / 1_000_000d;
        string current = currentBps <= 0 ? "No limit" : $"{currentMbps:0.#} Mbps";
        string recommended = option.RecommendedLimitMbps == 0 ? "No limit needed" : $"{option.RecommendedLimitMbps} Mbps";
        string why = $"Your server's upload measured {test.UploadMbps:0.#} Mbps on {test.TestedUtc.ToLocalTime():d MMM}. With two people watching away from home, each gets about {option.PerViewerMbps:0.#} Mbps: {option.Quality.ToLowerInvariant()}. Change the number of viewers on the Tests tab.";
        const string where = "Dashboard → Playback → Streaming → Internet streaming bitrate limit";

        if (option.RecommendedLimitMbps == 0)
        {
            return;
        }

        if (currentBps <= 0)
        {
            Add(AreaServer, Sev.Improve, "No remote streaming limit, but your upload can't keep up with full quality", current, recommended,
                why + " Without a limit, apps can ask for more than your connection can send, which causes buffering.", where);
        }
        else if (currentMbps > option.RecommendedLimitMbps * 1.25)
        {
            Add(AreaServer, Sev.Improve, "The remote streaming limit is higher than your upload can deliver", current, recommended, why, where);
        }
        else if (currentMbps < option.RecommendedLimitMbps * 0.6)
        {
            Add(AreaServer, Sev.Tip, "You could allow better quality away from home", current, recommended,
                why + " Your current limit transcodes more than it needs to.", where);
        }
    }
}

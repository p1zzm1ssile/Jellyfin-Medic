using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Reads every installed plugin's status and settings file.
///
/// Every plugin stores its settings differently, so there's no universal "right" value.
/// What this can do for any plugin: spot broken or disabled plugins, unreadable settings files,
/// leftover files from removed plugins, debug logging left on, thread counts above your CPU,
/// very short repeat intervals, and connection settings left empty. Advice for a specific
/// plugin's settings needs rules written for that plugin.
/// </summary>
public static class PluginAuditor
{
    private const string Area = "Plugins";
    private const string Where = "Dashboard → Plugins → (plugin) → Settings";

    public static List<PluginReport> Audit(IPluginManager pluginManager, IApplicationPaths paths, PluginContext context)
    {
        int cpuThreads = context.CpuThreads;
        var reports = new List<PluginReport>();
        var configPaths = new Dictionary<PluginReport, string>();
        var usedConfigFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string configDir = paths.PluginConfigurationsPath;

        foreach (var plugin in pluginManager.Plugins.Cast<object>())
        {
            var report = new PluginReport
            {
                Name = SettingsReader.Text(plugin, "Name") ?? "Unknown plugin",
                Version = SettingsReader.Text(plugin, "Version") ?? string.Empty,
                Status = SettingsReader.Text(plugin, "Manifest.Status") ?? "Unknown"
            };

            CheckStatus(report);

            string? configFile = FindConfigFile(plugin, configDir);
            if (configFile is not null)
            {
                usedConfigFiles.Add(configFile);
                report.ConfigFile = Path.GetFileName(configFile);
                ReadSettings(report, configFile, cpuThreads);
                configPaths[report] = configFile;
            }

            reports.Add(report);
        }

        ApplyPluginRules(configPaths, context);

        // Settings files that no installed plugin owns.
        try
        {
            if (Directory.Exists(configDir))
            {
                var orphans = Directory.GetFiles(configDir, "*.xml")
                    .Where(f => !usedConfigFiles.Contains(f))
                    .Select(f => Path.GetFileName(f))
                    .Where(n => n is not null)
                    .ToList();

                if (orphans.Count > 0)
                {
                    var holder = new PluginReport { Name = "Leftover settings files", Status = "Not installed" };
                    holder.Findings.Add(new Finding
                    {
                        Area = Area,
                        Severity = Sev.Tip,
                        Title = orphans.Count == 1 ? "A settings file doesn't belong to any installed plugin" : $"{orphans.Count} settings files don't belong to any installed plugin",
                        Current = string.Join(", ", orphans),
                        Recommended = "Delete them if you've uninstalled those plugins",
                        Why = "They're usually left behind by removed plugins. Harmless, but if you reinstall a plugin it picks up its old settings.",
                        Where = $"{configDir} on your server"
                    });
                    reports.Add(holder);
                }
            }
        }
        catch
        {
            // Folder unreadable: skip the leftover check.
        }

        return reports
            .OrderBy(r => r.Findings.Count == 0 ? 1 : 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Runs the plugin-specific rules for every active plugin that has a settings file.</summary>
    private static void ApplyPluginRules(Dictionary<PluginReport, string> configPaths, PluginContext context)
    {
        var active = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (report, path) in configPaths)
        {
            if (!string.Equals(report.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (XDocument.Load(path).Root is { } root)
                {
                    active[Path.GetFileName(path)] = root;
                }
            }
            catch
            {
                // Unreadable files are already reported by ReadSettings.
            }
        }

        foreach (var (report, path) in configPaths)
        {
            string file = Path.GetFileName(path);
            if (!active.TryGetValue(file, out var root))
            {
                continue;
            }

            try
            {
                PluginRules.Apply(report, file, root, active, context);
            }
            catch (Exception ex)
            {
                report.Findings.Add(new Finding
                {
                    Area = Area,
                    Severity = Sev.Tip,
                    Title = $"{report.Name}: some settings checks couldn't run",
                    Current = ex.GetType().Name,
                    Why = $"Medic hit an error reading this plugin's settings ({ex.Message}). Its other checks are unaffected.",
                    Where = Where
                });
            }
        }
    }

    private static void CheckStatus(PluginReport report)
    {
        switch (report.Status)
        {
            case "Malfunctioned":
                Add(report, Sev.Problem, "failed to load", report.Status, "Update or reinstall it, or remove it",
                    "Jellyfin couldn't start this plugin, so none of its features are working. The Jellyfin log will say why.");
                break;
            case "NotSupported":
                Add(report, Sev.Problem, "isn't compatible with your Jellyfin version", report.Status, "Install a version built for your Jellyfin",
                    "It was built for a different Jellyfin version and has been switched off.");
                break;
            case "Restart":
                Add(report, Sev.Tip, "is waiting for a restart", report.Status, "Restart Jellyfin",
                    "A change or update to this plugin only takes effect after a restart.");
                break;
            case "Disabled":
                Add(report, Sev.Tip, "is disabled", report.Status, "Uninstall it if you no longer need it",
                    "Disabled plugins still take up space and keep their settings.");
                break;
        }
    }

    private static void ReadSettings(PluginReport report, string file, int cpuThreads)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(file);
        }
        catch (Exception ex)
        {
            Add(report, Sev.Problem, "has an unreadable settings file", Path.GetFileName(file), "Reset the plugin's settings (delete the file, then restart)",
                $"The file isn't valid XML ({ex.Message}), so the plugin may be running on defaults or failing to start.");
            return;
        }

        if (doc.Root is null)
        {
            return;
        }

        foreach (var child in ChildrenWithNames(doc.Root))
        {
            FlattenXml(child.Element, child.Name, report.Settings);
            if (report.Settings.Count > 400)
            {
                break;
            }
        }

        var debug = new List<string>();
        var threads = new List<string>();
        var intervals = new List<string>();
        var empty = new List<string>();
        int filledConnections = 0;

        foreach (var row in report.Settings)
        {
            string leaf = SettingsReader.LeafName(row.Name);
            string lower = leaf.ToLowerInvariant();
            string raw = row.Value;
            long? number = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

            if ((lower.Contains("debug", StringComparison.Ordinal) || lower.Contains("verbose", StringComparison.Ordinal)) && raw == "On")
            {
                debug.Add(leaf);
            }

            if ((lower.Contains("thread", StringComparison.Ordinal) || lower.Contains("parallel", StringComparison.Ordinal) ||
                 lower.Contains("concurren", StringComparison.Ordinal) || lower.Contains("maxdegree", StringComparison.Ordinal) ||
                 lower.Contains("workers", StringComparison.Ordinal)) && number > cpuThreads)
            {
                threads.Add($"{leaf} = {number}");
            }

            if (lower.Contains("interval", StringComparison.Ordinal) && number > 0 &&
                ((lower.Contains("minute", StringComparison.Ordinal) && number < 15) ||
                 (lower.Contains("second", StringComparison.Ordinal) && number < 300) ||
                 (lower.Contains("hour", StringComparison.Ordinal) && number < 1)))
            {
                intervals.Add($"{leaf} = {number}");
            }

            bool connectionSetting = lower.EndsWith("url", StringComparison.Ordinal) || lower.EndsWith("server", StringComparison.Ordinal) ||
                                     lower.EndsWith("host", StringComparison.Ordinal) || lower.EndsWith("apikey", StringComparison.Ordinal);
            if (connectionSetting && OptionalKey(report.Name, lower))
            {
                // A key the plugin works fine without (it ships with its own), so blank is normal.
                continue;
            }

            if (connectionSetting)
            {
                if (raw is "(empty)" or "(not set)")
                {
                    empty.Add(leaf);
                }
                else
                {
                    filledConnections++;
                }
            }
        }

        if (debug.Count > 0)
        {
            Add(report, Sev.Improve, "has debug logging on", string.Join(", ", debug.Distinct()), "Off",
                "Debug logging fills the log folder quickly and can slow the plugin. Only use it while troubleshooting.");
        }

        if (threads.Count > 0)
        {
            Add(report, Sev.Improve, "is set to use more threads than your CPU has", string.Join(", ", threads.Distinct()), $"No more than {cpuThreads}",
                "Running more work in parallel than you have threads makes it slower, and competes with playback.");
        }

        if (intervals.Count > 0)
        {
            Add(report, Sev.Tip, "repeats some work very often", string.Join(", ", intervals.Distinct()), "Longer intervals, unless you need it that often",
                "Very short repeat intervals keep the server busy around the clock.");
        }

        if (empty.Count > 0 && filledConnections == 0)
        {
            Add(report, Sev.Tip, "may not be set up", "Blank: " + string.Join(", ", empty.Distinct()),
                "Fill in its connection details, or click Ignore if you don't use the plugin",
                "All of its connection settings (addresses or keys) are blank, which usually means it hasn't been configured yet.");
        }
        else if (empty.Count > 0)
        {
            // Other connection settings are filled in, so these probably belong to optional
            // features (for example Xtream Library's Dispatcharr address).
            Add(report, Sev.Tip, "has some blank connection settings", "Blank: " + string.Join(", ", empty.Distinct()),
                "Nothing to do if you don't use those features. Click Ignore if the plugin is working fine",
                "These look like addresses or keys for optional features. The plugin has other connection settings filled in, so it's probably working.");
        }
    }

    // Metadata plugins that come with their own API key: a blank key setting just means "use the built-in one".
    private static readonly string[] BuiltInKeyPlugins = { "tmdb", "omdb", "fanart", "tvdb", "audiodb", "musicbrainz" };

    private static bool OptionalKey(string pluginName, string settingLower)
    {
        if (settingLower.Contains("personal", StringComparison.Ordinal) && settingLower.EndsWith("apikey", StringComparison.Ordinal))
        {
            return true;
        }

        string name = (pluginName ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        return settingLower.EndsWith("apikey", StringComparison.Ordinal) && BuiltInKeyPlugins.Any(p => name.Contains(p, StringComparison.Ordinal));
    }

    private static string? FindConfigFile(object plugin, string configDir)
    {
        var candidates = new List<string>();

        if (SettingsReader.Get(plugin, "Instance") is { } instance)
        {
            if (SettingsReader.Text(instance, "ConfigurationFilePath") is { Length: > 0 } explicitPath)
            {
                candidates.Add(explicitPath);
            }

            candidates.Add(Path.Combine(configDir, instance.GetType().Assembly.GetName().Name + ".xml"));
        }

        foreach (var dll in SettingsReader.List(plugin, "DllFiles") ?? new List<string>())
        {
            candidates.Add(Path.Combine(configDir, Path.GetFileNameWithoutExtension(dll) + ".xml"));
        }

        return candidates.FirstOrDefault(c => File.Exists(c));
    }

    private static IEnumerable<(XElement Element, string Name)> ChildrenWithNames(XElement parent)
    {
        var children = parent.Elements().ToList();
        var totals = children.GroupBy(c => c.Name.LocalName).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        foreach (var child in children)
        {
            string name = child.Name.LocalName;
            if (totals[name] > 1)
            {
                int index = seen.TryGetValue(name, out var i) ? i : 0;
                seen[name] = index + 1;
                yield return (child, $"{name}[{index}]");
            }
            else
            {
                yield return (child, name);
            }
        }
    }

    private static void FlattenXml(XElement element, string path, List<SettingRow> rows)
    {
        if (rows.Count > 400)
        {
            return;
        }

        if (!element.HasElements)
        {
            rows.Add(new SettingRow { Section = "Plugin", Name = path, Value = SettingsReader.Display(path, element.Value) });
            return;
        }

        foreach (var child in ChildrenWithNames(element))
        {
            FlattenXml(child.Element, $"{path}.{child.Name}", rows);
        }
    }

    private static void Add(PluginReport report, string severity, string problem, string current, string recommended, string why) =>
        report.Findings.Add(new Finding
        {
            Area = Area,
            Severity = severity,
            Title = $"{report.Name} {problem}",
            Current = current,
            Recommended = recommended,
            Why = why,
            Where = WhereFor(report.Name)
        });

    // "Dashboard → Plugins → TMDb → Settings" rather than a placeholder.
    private static string WhereFor(string? pluginName) =>
        string.IsNullOrWhiteSpace(pluginName) ? Where : $"Dashboard → Plugins → {pluginName} → Settings";
}

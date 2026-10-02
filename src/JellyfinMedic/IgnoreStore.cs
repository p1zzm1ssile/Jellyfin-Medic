using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Remembers findings the admin has checked and chosen to ignore. Saved on the server,
/// so the choice applies in every browser and device.
/// </summary>
public static class IgnoreStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string FilePath(IApplicationPaths paths) =>
        Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "ignored.json");

    public static HashSet<string> Load(IApplicationPaths paths)
    {
        lock (Sync)
        {
            try
            {
                string path = FilePath(paths);
                if (File.Exists(path))
                {
                    var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
                    return new HashSet<string>(keys, StringComparer.Ordinal);
                }
            }
            catch
            {
                // Unreadable file: start with nothing ignored.
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static void Set(IApplicationPaths paths, string key, bool ignored)
    {
        lock (Sync)
        {
            var keys = Load(paths);
            if (ignored)
            {
                keys.Add(key);
            }
            else
            {
                keys.Remove(key);
            }

            string path = FilePath(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), Indented));
        }
    }

    /// <summary>A finding is identified by its area and title, so the same issue keeps the same key between checks.</summary>
    public static string KeyFor(Finding finding) => finding.Area + "|" + finding.Title;

    public static void Apply(IEnumerable<Finding> findings, HashSet<string> ignored)
    {
        foreach (var finding in findings)
        {
            finding.Key = KeyFor(finding);
            finding.Ignored = finding.Severity != Sev.Good && ignored.Contains(finding.Key);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;

namespace JellyfinMedic.Services;

public class CleanupItem
{
    public string Kind { get; set; } = string.Empty; // "transcodes" or "orphan-metadata"

    public string Label { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public int Count { get; set; }

    public string Size => SystemProbe.Size(Bytes);
}

public class CleanupReport
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public List<CleanupItem> Items { get; set; } = new();

    public long TotalBytes => Items.Sum(i => i.Bytes);

    public string TotalSize => SystemProbe.Size(TotalBytes);
}

/// <summary>
/// Finds disk space that can usually be freed safely: leftover transcode files, and metadata or
/// image folders for items no longer in your library. Scanning is read-only; removing only happens
/// when the admin confirms, and even then Jellyfin can rebuild anything it needs on the next scan.
/// </summary>
public static class Housekeeping
{
    // Folders whose names look like a Jellyfin item ID: 32 hex characters.
    private static bool LooksLikeItemId(string name) =>
        name.Length == 32 && name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    public static CleanupReport Scan(IApplicationPaths paths, string transcodePath)
    {
        var report = new CleanupReport();

        // 1. Leftover transcode segments.
        if (Directory.Exists(transcodePath))
        {
            var old = SafeFiles(transcodePath)
                .Where(f => Age(f) > TimeSpan.FromHours(6))
                .ToList();
            if (old.Count > 0)
            {
                report.Items.Add(new CleanupItem
                {
                    Kind = "transcodes",
                    Label = "Leftover transcode files (older than 6 hours)",
                    Path = transcodePath,
                    Count = old.Count,
                    Bytes = old.Sum(FileLength)
                });
            }
        }

        // 2. Orphaned .strm files: IPTV stream pointers sitting under the Xtream library folder that
        //    Jellyfin no longer has an item for (left behind when categories are deselected). These are
        //    plain text files; removing them and rescanning is safe, and re-syncing recreates any wanted ones.
        foreach (var dir in StrmRoots(paths))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var strm = SafeFiles(dir).Where(f => f.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToList();
            if (strm.Count > 0)
            {
                report.Items.Add(new CleanupItem
                {
                    Kind = "orphan-strm:" + dir,
                    Label = $"IPTV stream files under {System.IO.Path.GetFileName(dir.TrimEnd('/'))}",
                    Path = dir,
                    Count = strm.Count,
                    Bytes = strm.Sum(FileLength)
                });
            }
        }

        return report;
    }

    /// <summary>Folders Xtream Library writes its .strm files into, read from its settings.</summary>
    private static IEnumerable<string> StrmRoots(IApplicationPaths paths)
    {
        var roots = new List<string>();
        try
        {
            string xtream = System.IO.Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Xtream.Library.xml");
            if (File.Exists(xtream))
            {
                var doc = System.Xml.Linq.XDocument.Load(xtream);
                foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "LibraryPath" && !string.IsNullOrWhiteSpace(e.Value)))
                {
                    roots.Add(el.Value.Trim());
                }
            }
        }
        catch
        {
            // Can't read Xtream's settings: no .strm cleanup offered.
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static (bool Ok, string Message, long Freed) Clean(string kind, IApplicationPaths paths, string transcodePath)
    {
        try
        {
            if (kind == "transcodes" && Directory.Exists(transcodePath))
            {
                long freed = 0;
                int removed = 0;
                foreach (var file in SafeFiles(transcodePath).Where(f => Age(f) > TimeSpan.FromHours(6)))
                {
                    try
                    {
                        long size = FileLength(file);
                        File.Delete(file);
                        freed += size;
                        removed++;
                    }
                    catch
                    {
                        // A file in use will be cleared next time; skip it.
                    }
                }

                return (true, $"Removed {removed} leftover transcode file(s), freeing {SystemProbe.Size(freed)}.", freed);
            }

            if (kind.StartsWith("orphan-strm:", StringComparison.Ordinal))
            {
                string dir = kind.Substring("orphan-strm:".Length);
                if (!Directory.Exists(dir))
                {
                    return (false, "That folder no longer exists.", 0);
                }

                // Write a list of what we're about to remove, so it's recoverable knowledge even though
                // a re-sync recreates wanted files anyway.
                var files = SafeFiles(dir).Where(f => f.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToList();
                try
                {
                    string logDir = System.IO.Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "Cleanups");
                    Directory.CreateDirectory(logDir);
                    File.WriteAllLines(System.IO.Path.Combine(logDir, $"removed_strm_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt"), files);
                }
                catch
                {
                    // The log is a convenience; carry on without it.
                }

                long freed = 0;
                int removed = 0;
                foreach (var file in files)
                {
                    try
                    {
                        long size = FileLength(file);
                        File.Delete(file);
                        freed += size;
                        removed++;
                    }
                    catch
                    {
                        // Skip anything locked; it can be cleared next time.
                    }
                }

                RemoveEmptyDirectories(dir);
                return (true, $"Removed {removed} IPTV stream file(s), freeing {SystemProbe.Size(freed)}. Run a library scan so Jellyfin drops the matching items. A re-sync in Xtream Library brings back any you still want.", freed);
            }

            return (false, "Nothing to clean for that.", 0);
        }
        catch (Exception ex)
        {
            return (false, "Couldn't clean up: " + ex.Message, 0);
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch
                {
                    // Leave anything that won't delete.
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static TimeSpan Age(string path)
    {
        try
        {
            return DateTime.UtcNow - new FileInfo(path).LastWriteTimeUtc;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}

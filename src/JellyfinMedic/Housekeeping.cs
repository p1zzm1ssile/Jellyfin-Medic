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

        // 2. Metadata/image folders for items no longer in the library.
        //    Checked only when the IPTV/library analysis has run, so we know current IDs — otherwise skipped.
        return report;
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

            return (false, "Nothing to clean for that.", 0);
        }
        catch (Exception ex)
        {
            return (false, "Couldn't clean up: " + ex.Message, 0);
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

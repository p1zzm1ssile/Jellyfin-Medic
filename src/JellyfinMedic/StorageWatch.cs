using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>One drive Medic keeps an eye on, with its free space and how fast it's filling.</summary>
public sealed class DriveStatus
{
    public string Key { get; set; } = string.Empty;

    // What it holds, e.g. "Films, Series" or "Config & database".
    public string Holds { get; set; } = string.Empty;

    public string ExamplePath { get; set; } = string.Empty;

    public double FreeGb { get; set; }

    public double TotalGb { get; set; }

    // Gigabytes used per day, averaged over the days recorded. Null until there are 3 days.
    public double? GbPerDay { get; set; }

    public double? DaysToFull => GbPerDay is > 0.05 ? FreeGb / GbPerDay : null;
}

/// <summary>A day's free space on one drive.</summary>
public sealed class DiskSample
{
    public string Key { get; set; } = string.Empty;

    public string Day { get; set; } = string.Empty;

    public double FreeGb { get; set; }

    // A folder on that drive, so the background sampler can read it again tomorrow.
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Watches the drives Jellyfin uses (config, cache, transcodes and every library folder): free space,
/// and, from one reading a day kept for 30 days, how fast each is filling. Also measures the folders
/// that quietly grow (metadata, trickplay, the image cache) in the background, as they can hold
/// millions of files.
/// </summary>
public static class StorageWatch
{
    private const int KeepDays = 30;
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, long Bytes, bool Complete)> Sizes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> Measuring = new(StringComparer.Ordinal);

    private static string HistoryPath(string pluginConfigDir) => Path.Combine(pluginConfigDir, "JellyfinMedic", "disk_history.json");

    /// <summary>
    /// The distinct drives behind the given folders. Folders on the same drive are merged, so a
    /// library spread over several folders on one disk shows once.
    /// </summary>
    public static List<DriveStatus> Drives(IEnumerable<(string Holds, string Path)> folders)
    {
        var byKey = new Dictionary<string, DriveStatus>(StringComparer.Ordinal);
        foreach (var (holds, path) in folders)
        {
            if (string.IsNullOrWhiteSpace(path) || SystemProbe.Space(path) is not { } space || space.TotalGb <= 0)
            {
                continue;
            }

            // The mount point when it can be read; otherwise drives with the same size and free space are one.
            string key = SystemProbe.Mount(path)?.MountPoint
                ?? string.Create(CultureInfo.InvariantCulture, $"{space.TotalGb:0.0}/{space.FreeGb:0.0}");
            if (byKey.TryGetValue(key, out var existing))
            {
                if (!existing.Holds.Split(", ").Contains(holds, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Holds += ", " + holds;
                }

                continue;
            }

            byKey[key] = new DriveStatus { Key = key, Holds = holds, ExamplePath = path, FreeGb = space.FreeGb, TotalGb = space.TotalGb };
        }

        return byKey.Values.ToList();
    }

    /// <summary>Saves today's free space for each drive (once a day) and fills in how fast each is filling.</summary>
    public static void Record(string pluginConfigDir, List<DriveStatus> drives)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (Sync)
        {
            string path = HistoryPath(pluginConfigDir);
            var history = ScheduleStorage.ReadList<DiskSample>(path);
            bool changed = false;
            foreach (var d in drives)
            {
                if (!history.Any(h => h.Key == d.Key && h.Day == today))
                {
                    history.Add(new DiskSample { Key = d.Key, Day = today, FreeGb = d.FreeGb, Path = d.ExamplePath });
                    changed = true;
                }
            }

            string oldest = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            changed |= history.RemoveAll(h => string.CompareOrdinal(h.Day, oldest) < 0) > 0;
            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                ScheduleStorage.WriteJson(path, history);
            }

            foreach (var d in drives)
            {
                d.GbPerDay = FillRate(history.Where(h => h.Key == d.Key).OrderBy(h => h.Day).ToList());
            }
        }
    }

    private static string _recordedDay = string.Empty;

    /// <summary>
    /// Once a day, from the background sampler: reads the drives seen before again, so the fill rate
    /// keeps working on days nobody opens Medic.
    /// </summary>
    public static void RecordKnown(string pluginConfigDir)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_recordedDay == today)
        {
            return;
        }

        _recordedDay = today;
        List<DiskSample> history;
        lock (Sync)
        {
            history = ScheduleStorage.ReadList<DiskSample>(HistoryPath(pluginConfigDir));
        }

        var drives = history.Where(h => h.Path.Length > 0)
            .GroupBy(h => h.Key)
            .Select(g => g.OrderBy(h => h.Day).Last())
            .Select(h => SystemProbe.Space(h.Path) is { } s ? new DriveStatus { Key = h.Key, ExamplePath = h.Path, FreeGb = s.FreeGb, TotalGb = s.TotalGb } : null)
            .OfType<DriveStatus>()
            .ToList();
        if (drives.Count > 0)
        {
            Record(pluginConfigDir, drives);
        }
    }

    /// <summary>Gigabytes used per day between the first and last readings, once they're 3 days apart.</summary>
    public static double? FillRate(List<DiskSample> samples)
    {
        if (samples.Count < 2
            || !DateTime.TryParse(samples[0].Day, CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            || !DateTime.TryParse(samples[^1].Day, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
        {
            return null;
        }

        double days = (last - first).TotalDays;
        return days >= 3 ? Math.Round((samples[0].FreeGb - samples[^1].FreeGb) / days, 2) : null;
    }

    /// <summary>
    /// A folder's size from the last measurement (up to 12 hours old), starting a new one in the
    /// background when needed. Null until the first measurement finishes.
    /// </summary>
    public static (long Bytes, bool Complete)? FolderSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        bool fresh = Sizes.TryGetValue(path, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromHours(12);
        if (!fresh && Measuring.TryAdd(path, true))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    const int MaxFiles = 3_000_000;
                    long bytes = 0;
                    int files = 0;
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                    {
                        try
                        {
                            bytes += file.Length;
                        }
                        catch
                        {
                            // Gone already.
                        }

                        if (++files >= MaxFiles)
                        {
                            break;
                        }
                    }

                    Sizes[path] = (DateTime.UtcNow, bytes, files < MaxFiles);
                }
                catch
                {
                    // Unreadable: try again next time.
                }
                finally
                {
                    Measuring.TryRemove(path, out _);
                }
            });
        }

        return Sizes.TryGetValue(path, out var size) ? (size.Bytes, size.Complete) : null;
    }
}

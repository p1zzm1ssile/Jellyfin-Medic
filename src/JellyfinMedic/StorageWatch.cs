using System.Collections.Concurrent;
using System.Globalization;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>One drive Medic keeps an eye on, with its free space and how fast it's filling.</summary>
public sealed class DriveStatus
{
    public string Key { get; set; } = string.Empty;

    // The drive's mount point, when Medic could read it.
    public string? MountPoint { get; set; }

    // What it holds, e.g. "Films, Series" or "Config & database".
    public string Holds { get; set; } = string.Empty;

    public string ExamplePath { get; set; } = string.Empty;

    /// <summary>
    /// What its warnings are remembered by when ignored: the mount point, which tells apart two drives that hold the
    /// same libraries, or what it holds when the mount point couldn't be read (Key then changes with free space).
    /// </summary>
    public string StableName => MountPoint ?? Holds;

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

    // What the drive holds, e.g. "Films, Series". Empty in readings saved before it was recorded.
    public string Holds { get; set; } = string.Empty;

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

    // What WatchedDrives calls Jellyfin's own folders, which come before the libraries in a drive's Holds.
    private static readonly string[] SystemFolders = { "config & database", "cache", "transcodes" };

    /// <summary>Whether a drive's Holds starts with one of Jellyfin's own folders, rather than a library.</summary>
    internal static bool SystemFolder(string holds) => SystemFolders.Contains(holds.Split(", ")[0], StringComparer.Ordinal);

    /// <summary>
    /// The distinct drives behind the given folders. Folders on the same drive are merged, so a
    /// library spread over several folders on one disk shows once. A library folder that's missing
    /// or empty (a drive that didn't mount often leaves an empty mount point) is skipped, as its
    /// space would be the parent drive's; Jellyfin's own folders may not exist yet and are read through their parent.
    /// </summary>
    public static List<DriveStatus> Drives(IEnumerable<(string Holds, string Path)> folders) => Drives(folders, HasEntries);

    internal static List<DriveStatus> Drives(IEnumerable<(string Holds, string Path)> folders, Func<string, bool> libraryPresent)
    {
        var byKey = new Dictionary<string, DriveStatus>(StringComparer.Ordinal);
        var roots = new Lazy<List<string>>(DriveRoots);
        foreach (var (holds, path) in folders)
        {
            if (string.IsNullOrWhiteSpace(path) || (!SystemFolder(holds) && !libraryPresent(path))
                || SystemProbe.Space(path) is not { } space || space.TotalGb <= 0)
            {
                continue;
            }

            // Only when the drive can't be named are drives with the same size and free space one.
            string? mount = DriveOf(path, roots);
            string key = mount ?? string.Create(CultureInfo.InvariantCulture, $"{space.TotalGb:0.0}/{space.FreeGb:0.0}");
            if (byKey.TryGetValue(key, out var existing))
            {
                if (!existing.Holds.Split(", ").Contains(holds, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Holds += ", " + holds;
                }

                continue;
            }

            byKey[key] = new DriveStatus { Key = key, MountPoint = mount, Holds = holds, ExamplePath = path, FreeGb = space.FreeGb, TotalGb = space.TotalGb };
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
                var todays = history.FirstOrDefault(h => h.Key == d.Key && h.Day == today);
                if (todays is null)
                {
                    history.Add(new DiskSample { Key = d.Key, Day = today, FreeGb = d.FreeGb, Holds = d.Holds, Path = d.ExamplePath });
                    changed = true;
                }
                else if (todays.Holds.Length == 0 && d.Holds.Length > 0)
                {
                    // Today's first reading came from the background sampler, which may not know what the drive holds.
                    todays.Holds = d.Holds;
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

        // Only once it worked: a reading that failed is tried again in 5 minutes, not tomorrow.
        RecordKnownNow(pluginConfigDir);
        _recordedDay = today;
    }

    /// <summary>Reads the drives seen before again and saves today's reading of each, whatever the day.</summary>
    internal static void RecordKnownNow(string pluginConfigDir)
    {
        List<DiskSample> history;
        lock (Sync)
        {
            history = ScheduleStorage.ReadList<DiskSample>(HistoryPath(pluginConfigDir));
        }

        // Each folder goes through Drives again, so its drive is named as it is everywhere else today: readings once
        // kept under a key that followed free space come back under the drive's own.
        var drives = Drives(history.Where(h => h.Path.Length > 0)
            .GroupBy(h => h.Key)
            .Select(g => (g.OrderBy(h => h.Day).LastOrDefault(h => h.Holds.Length > 0)?.Holds ?? string.Empty, g.OrderBy(h => h.Day).Last().Path)));
        if (drives.Count > 0)
        {
            Record(pluginConfigDir, drives);
        }
    }

    /// <summary>
    /// Whether a library folder is there to be read: missing or empty, it's taken as a drive that didn't mount, whose
    /// empty mount point would report the parent drive's space. The Dashboard's Disks panel goes by the same test.
    /// </summary>
    internal static bool HasEntries(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The drive that holds <paramref name="path"/>, or null when it can't be named.</summary>
    public static string? DriveOf(string path) => DriveOf(path, new Lazy<List<string>>(DriveRoots));

    // The mount point when it can be read, else the volume that holds the folder (Windows, macOS).
    internal static string? DriveOf(string path, Lazy<List<string>> roots, Func<string, string?>? mountOf = null) =>
        (mountOf ?? (p => SystemProbe.Mount(p)?.MountPoint))(path) ?? VolumeRoot(path, roots.Value);

    /// <summary>
    /// The longest of <paramref name="roots"/> that holds <paramref name="path"/>, or null. Where mount points can't be
    /// read (Windows, macOS), this names the drive, and unlike its free space it stays the same as the drive fills.
    /// </summary>
    internal static string? VolumeRoot(string path, IEnumerable<string> roots) =>
        roots.Where(root => root.Length > 0
                            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            && (path.Length == root.Length || root[^1] is '/' or '\\' || path[root.Length] is '/' or '\\'))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

    private static List<string> DriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>The daily readings of the last 30 days, every drive's. A damaged file reads as none and is set aside.</summary>
    internal static List<DiskSample> History(string pluginConfigDir)
    {
        lock (Sync)
        {
            return ScheduleStorage.ReadList<DiskSample>(HistoryPath(pluginConfigDir));
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

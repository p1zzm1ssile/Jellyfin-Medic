using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>Checks: storage.</summary>
public sealed partial class DiagnosticsEngine
{
    // ---------- Storage ----------

    private List<DriveStatus>? _drives;

    /// <summary>Config, cache, transcode and library drives, each once, with today's reading saved.</summary>
    private List<DriveStatus> WatchedDrives(List<LibraryFacts> libraries, object? encoding)
    {
        if (_drives is not null)
        {
            return _drives;
        }

        var folders = new List<(string, string)>
        {
            ("config & database", _paths.DataPath),
            ("cache", _paths.CachePath),
            ("transcodes", TranscodePath(encoding))
        };
        folders.AddRange(LibraryFolders(libraries));
        _drives = StorageWatch.Drives(folders);
        StorageWatch.Record(_paths.PluginConfigurationsPath, _drives);
        return _drives;
    }

    // Each folder of a library on a drive (not a streamed one), with the library's name.
    private static IEnumerable<(string Holds, string Path)> LibraryFolders(List<LibraryFacts> libraries) =>
        libraries.Where(l => !l.IsStreamed).SelectMany(l => l.Locations.Select(loc => (l.Name, loc)));

    internal enum LibraryFolderState
    {
        Listed,
        Empty,
        Denied,
        Failed,
        Gone
    }

    /// <summary>
    /// Whether a library folder lists with something in it, lists empty, may not be listed, fails to list (with the
    /// error), or doesn't exist.
    /// </summary>
    internal static (LibraryFolderState State, string Error) ReadLibraryFolder(string path) => ReadLibraryFolder(path, Directory.EnumerateFileSystemEntries);

    // An overload rather than an optional parameter, so ReadLibraryFolder stays a Func<string, ...> for UnreadLibraryFindings.
    internal static (LibraryFolderState State, string Error) ReadLibraryFolder(string path, Func<string, IEnumerable<string>> list)
    {
        try
        {
            return (list(path).Any() ? LibraryFolderState.Listed : LibraryFolderState.Empty, string.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            return (LibraryFolderState.Gone, string.Empty);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (LibraryFolderState.Denied, ex.Message);
        }
        catch (IOException ex)
        {
            return (LibraryFolderState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The library folders StorageWatch.Drives leaves out, as their space would be the parent drive's: one that may not
    /// be listed, one that fails to list, and an empty one whose drive was read before (an empty mount point, where a drive didn't mount).
    /// A new library that's still empty says nothing, and a folder that's gone has CheckLibraries' finding.
    /// </summary>
    internal static List<Finding> UnreadLibraryFindings(IEnumerable<(string Holds, string Path)> folders, IEnumerable<DiskSample> history, Func<string, (LibraryFolderState State, string Error)> read)
    {
        var readings = history.Select(h => h.Path).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        // A reading inside the folder means the folder held a drive then; one beside it says nothing about it.
        bool readBefore(string folder)
        {
            string inside = folder.TrimEnd('/', '\\');
            return readings.Any(p => p.Equals(inside, StringComparison.OrdinalIgnoreCase)
                                     || (p.StartsWith(inside, StringComparison.OrdinalIgnoreCase) && p.Length > inside.Length && p[inside.Length] is '/' or '\\'));
        }

        var findings = new List<Finding>();
        foreach (var library in folders.Where(f => !string.IsNullOrWhiteSpace(f.Path)).GroupBy(f => f.Holds, StringComparer.OrdinalIgnoreCase))
        {
            var states = library.Select(f => (f.Path, Read: read(f.Path))).ToList();
            var unreadable = states.Where(f => f.Read.State == LibraryFolderState.Denied).Select(f => f.Path).ToList();
            if (unreadable.Count > 0)
            {
                findings.Add(NewFinding(AreaStorage, Sev.Problem, $"The drive for {library.Key} can't be read", "Jellyfin isn't allowed to list its folder",
                    HostPlatform.IsContainer
                        ? "Check the folder is mapped into the container and that the user Jellyfin runs as can read it"
                        : "Give the user Jellyfin runs as permission to read the folder",
                    "Medic can't check the drive's free space, and Jellyfin can't see what's in the folder, so a scan may mark its items as missing.",
                    string.Join(", ", unreadable)));
            }

            // An I/O error doesn't say why (a network share that stopped answering, a failing disk), so the finding gives the error.
            var failed = states.Where(f => f.Read.State == LibraryFolderState.Failed).ToList();
            if (failed.Count > 0)
            {
                findings.Add(NewFinding(AreaStorage, Sev.Problem, $"The drive for {library.Key} couldn't be read",
                    "Listing its folder failed: " + string.Join("; ", failed.Select(f => f.Read.Error).Distinct(StringComparer.Ordinal)),
                    "Check the drive or network share behind the folder is connected and answering",
                    "Medic can't check the drive's free space, and Jellyfin can't see what's in the folder, so a scan may mark its items as missing.",
                    string.Join(", ", failed.Select(f => f.Path))));
            }

            var unmounted = states.Where(f => f.Read.State == LibraryFolderState.Empty && readBefore(f.Path)).Select(f => f.Path).ToList();
            if (unmounted.Count > 0)
            {
                findings.Add(NewFinding(AreaStorage, Sev.Problem, $"The drive for {library.Key} looks unmounted", "Its folder is empty, though Medic read the drive there before",
                    HostPlatform.IsContainer
                        ? "Check the drive is mounted, and mounted before the container starts"
                        : "Check the drive is connected and mounted",
                    "An empty folder where a drive was usually means the drive didn't mount. Medic can't check its free space, and a scan may mark its items as missing.",
                    string.Join(", ", unmounted)));
            }
        }

        return findings;
    }

    internal static string Weeks(double days) =>
        days < 14 ? (Math.Round(days) <= 1 ? "about 1 day" : $"about {Math.Round(days):0} days")
        : days < 120 ? $"about {Math.Round(days / 7):0} weeks" : $"about {Math.Round(days / 30):0} months";

    /// <summary>"Full", "nearly full" and "will be full" for each drive.</summary>
    internal static List<Finding> DriveSpaceFindings(IEnumerable<DriveStatus> drives)
    {
        var findings = new List<Finding>();
        // A drive that holds only transcodes is Jellyfin's scratch space, often a small RAM folder that reads as full
        // when empty and as filling up mid-transcode. CheckTranscoding judges its size.
        foreach (var d in drives.Where(d => d.Holds != "transcodes"))
        {
            string? verdict = SpaceVerdict(d.FreeGb, d.TotalGb, d.Holds);
            if (verdict == Sev.Problem)
            {
                findings.Add(NewFinding(AreaStorage, Sev.Problem, $"The drive for {d.Holds} is full", $"{SystemProbe.Gb(d.FreeGb)} free of {SystemProbe.Gb(d.TotalGb)}",
                    "Free some space or add a drive", "New downloads and track cleanup will fail, and Jellyfin can't save images or subtitles next to your files.", d.ExamplePath));
            }
            else if (verdict == Sev.Improve)
            {
                findings.Add(NewFinding(AreaStorage, Sev.Improve, $"The drive for {d.Holds} is nearly full", $"{SystemProbe.Gb(d.FreeGb)} free of {SystemProbe.Gb(d.TotalGb)}",
                    "Keep at least 25 GB free, or 3% of the drive up to 150 GB", "A full drive stops new files arriving and can stop Jellyfin saving artwork and subtitles.", d.ExamplePath));
            }

            if (d.DaysToFull is { } days && days < 30)
            {
                findings.Add(NewFinding(AreaStorage, days < 7 ? Sev.Problem : Sev.Improve, $"The drive for {d.Holds} will be full in {Weeks(days)}",
                    $"{SystemProbe.Gb(d.FreeGb)} free, using about {SystemProbe.Gb(d.GbPerDay!.Value)} a day",
                    "Free some space, add a drive, or find what's filling it (for example trickplay images or old downloads)",
                    "At this rate the drive runs out soon, and Jellyfin, downloads and track cleanup all need room to write.", d.ExamplePath,
                    id: "storage.filling-up:" + d.StableName));
            }
        }

        return findings;
    }

    /// <summary>
    /// Problem when the drive is full, Improve when nearly full, else null. Sizes are in GiB, as SystemProbe reads them.
    /// The share of the drive is capped so a 16 TB drive with 150 GB free isn't called full.
    /// </summary>
    internal static string? SpaceVerdict(double freeGb, double totalGb, string holds)
    {
        // The config and cache drives have their own checks in CheckStorage; the transcode drive has CheckTranscoding's.
        if (holds.Contains("config & database", StringComparison.Ordinal) || holds.Contains("cache", StringComparison.Ordinal) || holds == "transcodes")
        {
            return null;
        }

        if (freeGb < Math.Max(5, Math.Min(totalGb * 0.01, 50)))
        {
            return Sev.Problem;
        }

        return freeGb < Math.Max(25, Math.Min(totalGb * 0.03, 150)) ? Sev.Improve : null;
    }

    private void CheckStorage(List<LibraryFacts> libraries, object? encoding)
    {
        _report.Findings.AddRange(DriveSpaceFindings(WatchedDrives(libraries, encoding)));
        _report.Findings.AddRange(UnreadLibraryFindings(LibraryFolders(libraries), StorageWatch.History(_paths.PluginConfigurationsPath), ReadLibraryFolder));

        if (SystemProbe.Space(_paths.DataPath) is { } data)
        {
            if (data.FreeGb < 5)
            {
                Add(AreaStorage, Sev.Problem, "Almost no space left for the database", $"{SystemProbe.Gb(data.FreeGb)} free",
                    "Free up space, or move " + HostPlatform.DataFolder + " to a bigger drive", "If the database can't write, Jellyfin can corrupt it or stop working.", WhereDocker);
            }
            else if (data.FreeGb < 15)
            {
                Add(AreaStorage, Sev.Improve, "Low space on the config drive", $"{SystemProbe.Gb(data.FreeGb)} free",
                    "At least 15 GB free", "Metadata, images and database growth all land here.", WhereDocker);
            }
        }

        if (SystemProbe.Mount(_paths.DataPath) is { } dataMount && dataMount.FsType.StartsWith("fuse.shfs", StringComparison.Ordinal))
        {
            Add(AreaStorage, Sev.Improve, "Jellyfin's database goes through Unraid's user-share layer",
                "/config mapped to /mnt/user/appdata/...",
                "Map /config to /mnt/cache/appdata/... (or make appdata an exclusive share)",
                "The database does thousands of small reads and writes. The user-share layer (FUSE) slows each one down, which makes the web UI and library scans noticeably slower.",
                WhereDocker);
        }

        if (!SameDrive(StorageWatch.DriveOf(_paths.CachePath), StorageWatch.DriveOf(_paths.DataPath)) && SystemProbe.Space(_paths.CachePath) is { } cache && cache.FreeGb < 10)
        {
            Add(AreaStorage, Sev.Improve, "Low space on the cache drive", $"{SystemProbe.Gb(cache.FreeGb)} free", "At least 10 GB free",
                "Images and metadata are cached here; when it's full, artwork stops loading.", WhereDocker);
        }

        long wal = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db-wal"));
        if (wal > 512L * 1024 * 1024)
        {
            Add(AreaStorage, Sev.Improve, "The database's write-ahead log is large", SystemProbe.Size(wal),
                "Run the \"Optimize database\" task, and schedule it weekly",
                "A large log slows database reads until it's merged back in.", WhereTasks);
        }

        long logs = SystemProbe.DirectorySize(_paths.LogDirectoryPath);
        if (logs > 1024L * 1024 * 1024)
        {
            Add(AreaStorage, Sev.Improve, "Log files are taking a lot of space", SystemProbe.Size(logs),
                "Under 1 GB: delete old logs and check the log level below",
                "Big logs usually mean something is logging errors repeatedly, or debug logging is on.", "Dashboard → Logs");
        }

        string? level = LogLevel();
        if (level is "Debug" or "Verbose")
        {
            Add(AreaStorage, Sev.Improve, "Debug logging is on", level, "Information",
                "Debug logging writes far more and slows the server slightly. Only turn it on while troubleshooting.",
                "logging.json in your Jellyfin config folder");
        }
    }
}

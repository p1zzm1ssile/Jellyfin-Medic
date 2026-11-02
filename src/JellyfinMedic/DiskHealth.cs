using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Surfaces a failing or overheating drive under the disk Jellyfin's config or library sits on, read
/// from Unraid's own disk status file if it's visible to the container. Read-only; if the file isn't
/// there (not Unraid, or not mapped in), the check is simply skipped.
/// </summary>
public static class DiskHealth
{
    // Spinning disks are flagged from Unraid's default critical temperature. SSDs and NVMe drives are
    // made to run warmer, so they get a higher limit, around where NVMe drives commonly start to warn.
    private const int HotSpinning = 55;
    private const int HotSolidState = 70;

    // Unraid statuses that aren't a fault: a healthy disk, an empty slot, and a new disk added to an empty slot.
    // Matched whole: NP_MISSING and NP_DSBL have no device present either, but they're array disks that dropped out.
    private static readonly HashSet<string> HarmlessStatuses = new(StringComparer.OrdinalIgnoreCase) { "DISK_OK", "DISK_NP", "DISK_NEW" };

    private static readonly string[] DisksIniPaths =
    {
        "/var/local/emhttp/disks.ini",
        "/mnt/user/system/disks.ini"
    };

    public static List<Finding> Check()
    {
        string? path = DisksIniPaths.FirstOrDefault(File.Exists);
        return path is null ? new List<Finding>() : CheckFile(path);
    }

    /// <summary>One disks.ini: a disk is failing with errors or a bad status, and hot over its type's limit.</summary>
    internal static List<Finding> CheckFile(string path)
    {
        var findings = new List<Finding>();
        try
        {
            var failing = new List<string>();
            var hot = new List<string>();
            string name = string.Empty;
            string status = string.Empty;
            int errors = 0;
            int temp = 0;
            bool? rotational = null;

            void Flush()
            {
                if (name.Length == 0)
                {
                    return;
                }

                if (errors > 0)
                {
                    failing.Add($"{name}: {errors} read/write errors");
                }
                else if (status.StartsWith("DISK_", StringComparison.OrdinalIgnoreCase) && !HarmlessStatuses.Contains(status))
                {
                    failing.Add($"{name}: {status.Replace("DISK_", string.Empty)}");
                }

                // rotational is 1 for a spinning disk and 0 for an SSD or NVMe drive; without it, the stricter limit.
                if (temp >= (rotational == false ? HotSolidState : HotSpinning))
                {
                    hot.Add($"{name}: {temp}°C");
                }
            }

            foreach (var raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    Flush();
                    name = line.Trim('[', ']', '"');
                    status = string.Empty;
                    errors = 0;
                    temp = 0;
                    rotational = null;
                    continue;
                }

                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                string key = parts[0].Trim();
                string value = parts[1].Trim().Trim('"');
                if (key.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    status = value;
                }
                else if (key.Equals("numErrors", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var e))
                {
                    errors = e;
                }
                else if (key.Equals("temp", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var t))
                {
                    temp = t;
                }
                else if (key.Equals("rotational", StringComparison.OrdinalIgnoreCase))
                {
                    rotational = value switch { "1" => true, "0" => false, _ => null };
                }
            }

            Flush();

            if (failing.Count > 0)
            {
                findings.Add(new Finding
                {
                    Area = "Storage",
                    Severity = Sev.Problem,
                    Title = failing.Count == 1 ? "A disk may be failing" : $"{failing.Count} disks need attention",
                    Id = "storage.disks-failing",
                    Current = string.Join("; ", failing),
                    Recommended = "Check the drive in Unraid, and make sure your library and config are backed up",
                    Why = "Disk errors or a disk dropping out of the array risk losing data. Catching it early means you can replace the drive before it fails completely.",
                    Where = "Unraid → Main, and the drive's SMART report"
                });
            }

            if (hot.Count > 0)
            {
                findings.Add(new Finding
                {
                    Area = "Storage",
                    Severity = Sev.Problem,
                    Title = hot.Count == 1 ? "A disk is running hot" : $"{hot.Count} disks are running hot",
                    Id = "storage.disks-hot",
                    Current = string.Join("; ", hot),
                    Recommended = "Check the airflow around the drive, and the temperature limits Unraid uses for it",
                    Why = $"Heat wears a drive out sooner, and a drive that gets too hot slows down or stops to cool off. Spinning disks are flagged from {HotSpinning}°C, SSDs and NVMe drives from {HotSolidState}°C.",
                    Where = "Unraid → Main, and Settings → Disk Settings"
                });
            }
        }
        catch
        {
            // Unreadable status file: skip the check.
        }

        return findings;
    }
}

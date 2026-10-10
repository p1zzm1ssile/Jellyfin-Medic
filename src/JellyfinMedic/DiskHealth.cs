using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Surfaces a failing drive under the disk Jellyfin's config or library sits on, read from
/// Unraid's own disk status file if it's visible to the container. Read-only; if the file isn't
/// there (not Unraid, or not mapped in), the check is simply skipped.
/// </summary>
public static class DiskHealth
{
    private static readonly string[] DisksIniPaths =
    {
        "/var/local/emhttp/disks.ini",
        "/mnt/user/system/disks.ini"
    };

    public static List<Finding> Check()
    {
        var findings = new List<Finding>();
        string? path = DisksIniPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return findings;
        }

        try
        {
            var problems = new List<string>();
            string name = string.Empty;
            string status = string.Empty;
            int errors = 0;
            int temp = 0;

            void Flush()
            {
                if (name.Length == 0)
                {
                    return;
                }

                if (errors > 0)
                {
                    problems.Add($"{name}: {errors} read/write errors");
                }
                else if (status.Length > 0 && !status.Equals("DISK_OK", StringComparison.OrdinalIgnoreCase) && status.StartsWith("DISK_", StringComparison.OrdinalIgnoreCase) && !status.Contains("NP", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{name}: {status.Replace("DISK_", string.Empty)}");
                }
                else if (temp >= 55)
                {
                    problems.Add($"{name}: running hot ({temp}°C)");
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
            }

            Flush();

            if (problems.Count > 0)
            {
                findings.Add(new Finding
                {
                    Area = "Storage",
                    Severity = Sev.Problem,
                    Title = problems.Count == 1 ? "A disk may be failing" : $"{problems.Count} disks need attention",
                    Current = string.Join("; ", problems),
                    Recommended = "Check the drive in Unraid, and make sure your library and config are backed up",
                    Why = "Disk errors or a disk dropping out of the array risk losing data. Catching it early means you can replace the drive before it fails completely.",
                    Where = "Unraid → Main, and the drive's SMART report"
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

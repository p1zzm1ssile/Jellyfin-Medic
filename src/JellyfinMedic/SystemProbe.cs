using System.Globalization;

namespace JellyfinMedic.Services;

public sealed class HardwareInfo
{
    public int CpuThreads { get; set; }

    public string? CpuModel { get; set; }

    // Memory .NET is allowed to use, which respects a Docker memory limit.
    public double MemoryGb { get; set; }

    public double? HostMemoryGb { get; set; }

    public bool RandomHostname { get; set; }

    // intel, amd, nvidia, unknown or none
    public string GpuVendor { get; set; } = "none";

    public string? GpuModel { get; set; }

    public List<string> RenderNodes { get; set; } = new();

    public bool NvidiaDevice { get; set; }

    public string GpuDescription => GpuVendor switch
    {
        "intel" => "Intel graphics" + (GpuModel is null ? string.Empty : $" ({GpuModel})"),
        "amd" => "AMD graphics" + (GpuModel is null ? string.Empty : $" ({GpuModel})"),
        "nvidia" => GpuModel ?? "NVIDIA graphics",
        "unknown" => "A GPU (make not recognised)",
        _ => "None visible to Jellyfin"
    };
}

/// <summary>
/// Looks at the container from the inside. Everything here is read-only and wrapped so a
/// missing file just means "unknown" rather than an error.
/// </summary>
public static class SystemProbe
{
    private const double GiB = 1024d * 1024 * 1024;

    public static HardwareInfo Probe()
    {
        var info = new HardwareInfo
        {
            CpuThreads = Environment.ProcessorCount,
            MemoryGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / GiB, 1),
            RandomHostname = HostPlatform.HostnameIsRandom // one detector, so the two can't disagree
        };

        info.CpuModel = HostPlatform.Lines("/proc/cpuinfo")
            .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2).ElementAtOrDefault(1)?.Trim();

        if (info.CpuModel is null && OperatingSystem.IsWindows())
        {
            try
            {
                info.CpuModel = (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim();
            }
            catch
            {
                // Unknown is fine.
            }
        }

        var memLine = HostPlatform.Lines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        if (memLine is not null)
        {
            var kb = memLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            if (double.TryParse(kb, NumberStyles.Float, CultureInfo.InvariantCulture, out var kbValue))
            {
                info.HostMemoryGb = Math.Round(kbValue * 1024 / GiB, 1);
            }
        }

        // GPU devices passed into the container
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                info.RenderNodes = Directory.GetFiles("/dev/dri", "renderD*").OrderBy(p => p, StringComparer.Ordinal).ToList();
            }
        }
        catch
        {
            // Not readable: treat as no GPU nodes.
        }

        info.NvidiaDevice = NvidiaPresent(NvidiaDeviceNames(), Directory.Exists("/proc/driver/nvidia/gpus"), HostPlatform.IsContainer);

        var vendors = new List<(string Vendor, string? Device)>();
        foreach (var node in info.RenderNodes)
        {
            string name = Path.GetFileName(node);
            string? vendorId = ReadFirstLine($"/sys/class/drm/{name}/device/vendor");
            string? deviceId = ReadFirstLine($"/sys/class/drm/{name}/device/device");
            string vendor = vendorId?.Trim().ToLowerInvariant() switch
            {
                "0x8086" => "intel",
                "0x1002" => "amd",
                "0x10de" => "nvidia",
                null => "unknown",
                _ => "unknown"
            };
            vendors.Add((vendor, deviceId?.Trim()));
        }

        if (info.NvidiaDevice)
        {
            info.GpuVendor = "nvidia";
            info.GpuModel = NvidiaModel();
        }
        else if (vendors.Any(v => v.Vendor == "intel"))
        {
            info.GpuVendor = "intel";
            info.GpuModel = vendors.First(v => v.Vendor == "intel").Device is { } d ? $"device {d}" : null;
        }
        else if (vendors.Any(v => v.Vendor == "amd"))
        {
            info.GpuVendor = "amd";
            info.GpuModel = vendors.First(v => v.Vendor == "amd").Device is { } d ? $"device {d}" : null;
        }
        else if (info.RenderNodes.Count > 0)
        {
            info.GpuVendor = "unknown";
        }

        return info;
    }

    /// <summary>Whether an NVIDIA GPU is usable here, from the names of the /dev/nvidia* nodes and whether the driver lists GPUs in /proc.</summary>
    internal static bool NvidiaPresent(IEnumerable<string> devNames, bool procGpus, bool inContainer)
    {
        // /proc/driver/nvidia shows the host's driver even in a container given no GPU, and a container given only its
        // second GPU has /dev/nvidia1 but no /dev/nvidia0. On bare metal the nodes can appear only on first use.
        bool node = devNames.Any(n => n == "nvidiactl" || System.Text.RegularExpressions.Regex.IsMatch(n, @"^nvidia\d+$"));
        return node || (!inContainer && procGpus);
    }

    private static string[] NvidiaDeviceNames()
    {
        try
        {
            return Directory.GetFiles("/dev", "nvidia*").Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Free and total space on the drive holding a path.</summary>
    public static (double FreeGb, double TotalGb)? Space(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string existing = ExistingParent(path);
            var drive = new DriveInfo(existing);
            return (Math.Round(drive.AvailableFreeSpace / GiB, 1), Math.Round(drive.TotalSize / GiB, 1));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The mount a path lives on and its filesystem type, from /proc/mounts.
    /// "tmpfs" means RAM; "fuse.shfs" means an Unraid user share (/mnt/user).
    /// </summary>
    /// <summary>
    /// Whether the disk behind a path spins (a hard drive), from /sys/block. Null when it can't be
    /// told, for example on Windows, for network shares, or for pooled and virtual devices.
    /// </summary>
    public static bool? Spinning(string? path) => MountDevice(path) is { } device ? Spinning(device, HostPlatform.Lines, LinkTarget) : null;

    /// <summary>Whether a block device spins, from the given readers of /sys, so it can be worked out without the real disk.</summary>
    internal static bool? Spinning(string device, Func<string, string[]> lines, Func<string, string?> linkTarget)
    {
        if (!device.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return null;
        }

        string name = Path.GetFileName(device);
        foreach (string candidate in new[] { name, System.Text.RegularExpressions.Regex.Replace(name, @"(?<=\D)\d+$|(?<=nvme\d+n\d+)p\d+$|(?<=mmcblk\d+)p\d+$", string.Empty) })
        {
            // Virtual disks (virtio, Xen, QEMU, VMware, Hyper-V) say they spin even when the host has an SSD.
            string model = string.Join(' ', lines($"/sys/block/{candidate}/device/vendor").Concat(lines($"/sys/block/{candidate}/device/model")));
            if (candidate.StartsWith("vd", StringComparison.Ordinal) || candidate.StartsWith("xvd", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(model, "QEMU|VMware|Virtual|VBOX|Msft", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return null;
            }

            string file = $"/sys/block/{candidate}/queue/rotational";
            var text = lines(file).FirstOrDefault()?.Trim();
            if (text is "0" or "1")
            {
                // A USB bridge often says its disk spins whatever is behind it, so only a USB disk that says it doesn't is believed.
                if (text == "1" && linkTarget($"/sys/block/{candidate}")?.Contains("/usb", StringComparison.Ordinal) == true)
                {
                    return null;
                }

                return text == "1";
            }
        }

        return null;
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static string? MountDevice(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Mount(path) is not { } mount)
        {
            return null;
        }

        return HostPlatform.Lines("/proc/mounts")
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 3 && p[1].Replace("\\040", " ", StringComparison.Ordinal) == mount.MountPoint)
            .Select(p => p[0])
            .LastOrDefault();
    }

    public static (string MountPoint, string FsType)? Mount(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('/') + "/";
        }
        catch
        {
            return null;
        }

        (string MountPoint, string FsType)? best = null;
        foreach (var line in HostPlatform.Lines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3)
            {
                continue;
            }

            string mountPoint = parts[1].Replace("\\040", " ", StringComparison.Ordinal);
            string prefix = mountPoint.TrimEnd('/') + "/";
            if (full.StartsWith(prefix, StringComparison.Ordinal) && (best is null || mountPoint.Length >= best.Value.MountPoint.Length))
            {
                best = (mountPoint, parts[2]);
            }
        }

        return best;
    }

    public static long FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Total size of a folder, stopping after a set number of files so it stays quick.</summary>
    public static long DirectorySize(string? path, int maxFiles = 20000)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        int count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Skip files that vanish or can't be read.
                }

                if (++count >= maxFiles)
                {
                    break;
                }
            }
        }
        catch
        {
            // Partial total is fine.
        }

        return total;
    }

    public static string Gb(double gb) => gb >= 100 ? $"{gb:0} GB" : $"{gb:0.#} GB";

    public static string Size(long bytes)
    {
        if (bytes >= GiB)
        {
            return $"{bytes / GiB:0.#} GB";
        }

        return bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0} MB" : $"{bytes / 1024d:0} KB";
    }

    private static string ExistingParent(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return string.IsNullOrEmpty(current) ? "/" : current;
    }

    private static string? NvidiaModel()
    {
        try
        {
            if (!Directory.Exists("/proc/driver/nvidia/gpus"))
            {
                return null;
            }

            foreach (var dir in Directory.GetDirectories("/proc/driver/nvidia/gpus"))
            {
                var model = HostPlatform.Lines(Path.Combine(dir, "information"))
                    .FirstOrDefault(l => l.StartsWith("Model:", StringComparison.OrdinalIgnoreCase));
                if (model is not null)
                {
                    return model.Split(':', 2)[1].Trim();
                }
            }
        }
        catch
        {
            // Unknown model.
        }

        return null;
    }

    private static string? ReadFirstLine(string path) => HostPlatform.Lines(path).FirstOrDefault();
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JellyfinMedic.Services;

public sealed class HardwareInfo
{
    public int CpuThreads { get; set; }

    public string? CpuModel { get; set; }

    // Memory .NET is allowed to use, which respects a Docker memory limit.
    public double MemoryGb { get; set; }

    public double? HostMemoryGb { get; set; }

    public bool InDocker { get; set; }

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
            InDocker = File.Exists("/.dockerenv") || FileContains("/proc/1/cgroup", "docker")
        };

        info.CpuModel = ReadLines("/proc/cpuinfo")
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

        var memLine = ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
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

        info.NvidiaDevice = File.Exists("/dev/nvidia0") || Directory.Exists("/proc/driver/nvidia/gpus");

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
        foreach (var line in ReadLines("/proc/mounts"))
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

    /// <summary>True on Unraid. Kept for older callers; HostPlatform has the full picture.</summary>
    public static bool OnUnraid => HostPlatform.Kind == HostKind.Unraid;

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
                var model = ReadLines(Path.Combine(dir, "information"))
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

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadFirstLine(string path) => ReadLines(path).FirstOrDefault();

    private static bool FileContains(string path, string text) =>
        ReadLines(path).Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
}

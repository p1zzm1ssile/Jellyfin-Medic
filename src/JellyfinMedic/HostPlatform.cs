namespace JellyfinMedic.Services;

public enum HostKind
{
    Unraid,
    TrueNas,
    ProxmoxLxc,
    Docker,
    Linux,
    Windows,
    MacOs
}

/// <summary>
/// Works out where Jellyfin is running, so advice and "Where" directions fit that setup instead of
/// assuming one platform. Everything is read-only, worked out once, and falls back to plain "Linux".
/// </summary>
public static class HostPlatform
{
    private static HostKind? _kind;

    public static HostKind Kind => _kind ??= Detect();

    /// <summary>Linux of any kind. GPU devices (/dev/dri, /dev/nvidia0) and /proc are only visible here.</summary>
    public static bool IsLinux => Kind is not (HostKind.Windows or HostKind.MacOs);

    /// <summary>Running inside a container of any kind (Docker, Unraid, TrueNAS apps, Proxmox LXC).</summary>
    public static bool IsContainer => Kind is HostKind.Unraid or HostKind.TrueNas or HostKind.Docker or HostKind.ProxmoxLxc;

    /// <summary>A Docker-style container, where things like /dev/shm size and device flags apply.</summary>
    public static bool IsDockerLike => Kind is HostKind.Unraid or HostKind.TrueNas or HostKind.Docker;

    public static string Label => Kind switch
    {
        HostKind.Unraid => "Unraid (Docker)",
        HostKind.TrueNas => "TrueNAS SCALE (app)",
        HostKind.ProxmoxLxc => "Proxmox (LXC container)",
        HostKind.Docker => "Docker",
        HostKind.Windows => "Windows",
        HostKind.MacOs => "macOS",
        _ => "Linux"
    };

    /// <summary>Where to change how Jellyfin itself is run: devices, storage, memory.</summary>
    public static string WhereRunSettings => Kind switch
    {
        HostKind.Unraid => "Unraid → Docker → Jellyfin → Edit",
        HostKind.TrueNas => "TrueNAS → Apps → Jellyfin → Edit",
        HostKind.ProxmoxLxc => "Proxmox → your Jellyfin container → Resources (or its .conf file), then restart it",
        HostKind.Docker => "Your Jellyfin container's settings (docker run or Compose file), then recreate it",
        HostKind.Windows => "Windows: Jellyfin's install folders and the Jellyfin Server service",
        HostKind.MacOs => "macOS: the Jellyfin Server app and its data folder",
        _ => "Your server's system settings (drives, /etc/fstab, the jellyfin service)"
    };

    /// <summary>Jellyfin's own data folder (config, database, cache), in this platform's terms.</summary>
    public static string DataFolder => Kind switch
    {
        HostKind.Unraid => "Jellyfin's appdata folder",
        HostKind.TrueNas => "the app's config storage",
        HostKind.ProxmoxLxc => "the container's disk",
        HostKind.Docker => "the folder mapped to /config",
        HostKind.Windows => @"Jellyfin's data folder (usually C:\ProgramData\Jellyfin\Server)",
        HostKind.MacOs => "Jellyfin's data folder (~/Library/Application Support/jellyfin)",
        _ => "Jellyfin's data folder (usually /var/lib/jellyfin)"
    };

    /// <summary>How to give Jellyfin the GPU on this platform.</summary>
    public static string GpuSetup => Kind switch
    {
        HostKind.Unraid => "Intel/AMD: add the /dev/dri device. NVIDIA: install the Nvidia Driver plugin, then add --runtime=nvidia",
        HostKind.TrueNas => "Edit the app's GPU Configuration and pass your GPU through",
        HostKind.ProxmoxLxc => "Pass /dev/dri (and the /dev/nvidia* devices for NVIDIA) through to the container: Resources → Add → Device Passthrough",
        HostKind.Docker => "Intel/AMD: --device /dev/dri. NVIDIA: install the NVIDIA Container Toolkit and add --gpus all",
        HostKind.Windows => "Install your graphics card's latest driver, then choose NVENC (NVIDIA), QSV (Intel) or AMF (AMD) under Hardware acceleration",
        HostKind.MacOs => "Choose Apple VideoToolbox under Hardware acceleration",
        _ => "Install the GPU driver and add the jellyfin user to the render and video groups"
    };

    /// <summary>How to put transcodes in RAM here, or null where it isn't a sensible suggestion (Windows, macOS).</summary>
    public static string? RamTranscodeHow => Kind switch
    {
        HostKind.Unraid or HostKind.Docker => "Map a RAM folder (tmpfs, 4–8 GB) into the container and point the transcode path at it, with segment deletion on",
        HostKind.TrueNas => "Add a RAM (tmpfs) storage mount of 4–8 GB to the app and point the transcode path at it, with segment deletion on",
        HostKind.ProxmoxLxc => "Add a 4–8 GB tmpfs mount inside the container (for example in its /etc/fstab) and point the transcode path at it, with segment deletion on",
        HostKind.Linux => "Add a 4–8 GB tmpfs mount (for example in /etc/fstab) and point the transcode path at it, with segment deletion on",
        _ => null
    };

    /// <summary>How to make a RAM transcode folder bigger.</summary>
    public static string BiggerRamFolder => Kind switch
    {
        HostKind.Unraid => "add --shm-size=4g to Extra Parameters, or map a larger tmpfs",
        HostKind.Docker => "--shm-size=4g (shm_size: 4gb in Compose), or a larger tmpfs mount",
        _ => "a larger tmpfs mount"
    };

    private static HostKind Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return HostKind.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return HostKind.MacOs;
        }

        bool unraid = false, trueNas = false;
        foreach (var line in Lines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3)
            {
                continue;
            }

            // Unraid: a user share (fuse.shfs) or an array disk (/dev/md1, /dev/md1p1...).
            if (parts[2].StartsWith("fuse.shfs", StringComparison.Ordinal)
                || (parts[0].StartsWith("/dev/md", StringComparison.Ordinal) && parts[0].Length > 7 && char.IsDigit(parts[0][7])))
            {
                unraid = true;
            }

            // TrueNAS SCALE apps keep their storage in an ix-apps (or older ix-applications) dataset.
            if (parts[0].Contains("ix-apps", StringComparison.Ordinal) || parts[0].Contains("ix-applications", StringComparison.Ordinal)
                || parts[1].Contains("/.ix-apps", StringComparison.Ordinal))
            {
                trueNas = true;
            }
        }

        if (unraid)
        {
            return HostKind.Unraid;
        }

        if (trueNas)
        {
            return HostKind.TrueNas;
        }

        // Docker inside a Proxmox LXC is still set up through Docker, so Docker wins.
        if (File.Exists("/.dockerenv") || Lines("/proc/1/cgroup").Any(l => l.Contains("docker", StringComparison.OrdinalIgnoreCase)))
        {
            return HostKind.Docker;
        }

        string container = Lines("/run/systemd/container").FirstOrDefault()?.Trim() ?? string.Empty;
        if (container.Equals("lxc", StringComparison.OrdinalIgnoreCase)
            || Lines("/proc/1/environ").Any(l => l.Contains("container=lxc", StringComparison.Ordinal)))
        {
            return HostKind.ProxmoxLxc;
        }

        return HostKind.Linux;
    }

    private static string[] Lines(string path)
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
}

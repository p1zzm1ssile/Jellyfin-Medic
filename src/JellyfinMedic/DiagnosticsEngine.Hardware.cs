using System.Globalization;
using MediaBrowser.Controller;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>Checks: the server's specs, hardware and transcoding.</summary>
public sealed partial class DiagnosticsEngine
{
    // ---------- Specs ----------

    private void AddSpecs(HardwareInfo hw, object? encoding, List<LibraryFacts> libraries, int pluginCount)
    {
        string version = SettingsReader.ServerVersion(_host);
        Spec("Server", "Jellyfin version", version);
        Spec("Server", "Running on", HostPlatform.Label);
        Spec("Server", "Server name", string.IsNullOrWhiteSpace(_config.Configuration.ServerName) ? "(not set)" : _config.Configuration.ServerName);

        Spec("Hardware", "CPU", hw.CpuModel ?? "unknown");
        Spec("Hardware", "CPU threads available", hw.CpuThreads.ToString(CultureInfo.InvariantCulture));
        Spec("Hardware", "Memory Jellyfin can use", SystemProbe.Gb(hw.MemoryGb));
        if (hw.HostMemoryGb is { } host)
        {
            Spec("Hardware", "Memory in the machine", SystemProbe.Gb(host));
        }

        if (HostPlatform.IsLinux)
        {
            Spec("Hardware", "GPU", hw.GpuDescription);
            Spec("Hardware", HostPlatform.IsContainer ? "GPU devices in the container" : "GPU devices",
                hw.RenderNodes.Count == 0 && !hw.NvidiaDevice ? "None" : string.Join(", ", hw.RenderNodes.Concat(hw.NvidiaDevice ? new[] { "/dev/nvidia0" } : Array.Empty<string>())));
        }
        else
        {
            Spec("Hardware", "GPU", $"Not checked on {HostPlatform.Label} (see Hardware acceleration)");
        }
        Spec("Hardware", "Hardware acceleration", Nice(SettingsReader.Text(encoding, "HardwareAccelerationType")) ?? "unknown");

        SpaceSpec("Storage", "Config & database drive", _paths.DataPath);
        SpaceSpec("Storage", "Cache drive (images, metadata)", _paths.CachePath);
        string transcodePath = TranscodePath(encoding);
        SpaceSpec("Storage", $"Transcode folder ({transcodePath})", transcodePath);

        long db = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db"));
        long wal = SystemProbe.FileSize(Path.Combine(_paths.DataPath, "jellyfin.db-wal"));
        if (db > 0)
        {
            Spec("Storage", "Database size", $"{SystemProbe.Size(db)} (+ {SystemProbe.Size(wal)} write-ahead log)");
        }

        Spec("Storage", "Log folder size", SystemProbe.Size(SystemProbe.DirectorySize(_paths.LogDirectoryPath)));

        // Every library drive, and how fast each drive is filling.
        foreach (var d in WatchedDrives(libraries, encoding))
        {
            string rate = d.GbPerDay switch
            {
                null => "fill rate known after 3 days",
                <= 0.05 => "not filling up",
                { } r => $"using about {SystemProbe.Gb(r)} a day" + (d.DaysToFull is { } days ? $", full in {Weeks(days)}" : string.Empty)
            };
            Spec("Storage", $"Drive for {d.Holds}", $"{SystemProbe.Gb(d.FreeGb)} free of {SystemProbe.Gb(d.TotalGb)} ({rate})");
        }

        // Folders that quietly grow. Measured in the background, as they can hold millions of files.
        var serverPaths = _paths as IServerApplicationPaths;
        foreach (var (label, path) in new[]
                 {
                     ("Metadata folder", serverPaths?.InternalMetadataPath),
                     ("Trickplay images", _paths.TrickplayPath),
                     ("Image cache", _paths.ImageCachePath),
                     ("Cache folder (all)", _paths.CachePath)
                 })
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                continue;
            }

            Spec("Storage", label, StorageWatch.FolderSize(path) is { } size
                ? SystemProbe.Size(size.Bytes) + (size.Complete ? string.Empty : " or more")
                : "measuring, check back in a few minutes");
        }

        Spec("Libraries", "Libraries", libraries.Count.ToString(CultureInfo.InvariantCulture));
        long total = libraries.Sum(l => l.ItemCount ?? 0);
        Spec("Libraries", "Total items", total.ToString("N0", CultureInfo.InvariantCulture));
        foreach (var lib in libraries)
        {
            string count = lib.ItemCount is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) + " items" : "count unavailable";
            Spec("Libraries", lib.Name, count + (lib.IsStreamed ? " (mostly .strm streams)" : string.Empty));
        }

        Spec("Other", "Plugins installed", pluginCount.ToString(CultureInfo.InvariantCulture));
        Spec("Other", "Scheduled tasks", _tasks.ScheduledTasks.Count.ToString(CultureInfo.InvariantCulture));
    }

    // ---------- Hardware & transcoding ----------

    /// <summary>The checks that follow from the hardware acceleration setting, on plain inputs so tests can run them as any platform.</summary>
    internal static List<Finding> AccelerationChecks(HardwareInfo hw, object enc, object server, bool isLinux, bool isContainer)
    {
        var findings = new List<Finding>();
        string hwType = (SettingsReader.Text(enc, "HardwareAccelerationType") ?? "none").Trim().ToLowerInvariant();
        bool hwOn = hwType is not ("" or "none");

        (string Value, string Label) suggested = hw.GpuVendor switch
        {
            "intel" => ("qsv", "Intel QuickSync (QSV)"),
            "amd" => ("vaapi", "Video Acceleration API (VAAPI)"),
            "nvidia" => ("nvenc", "NVIDIA NVENC"),
            _ => (string.Empty, string.Empty)
        };

        if (!hwOn && !isLinux)
        {
            // Windows and macOS don't expose the GPU the way Linux does, so Medic can't see which one you
            // have. Point at the setting instead of guessing.
            findings.Add(NewFinding(AreaHardware, Sev.Tip, "Hardware acceleration is off", "None (everything on the CPU)",
                HostPlatform.GpuSetup,
                "If this computer has a graphics card or built-in graphics, Jellyfin can use it for transcodes, chapter images and trickplay instead of the CPU.",
                WhereTranscoding));
        }
        else if (!hwOn && suggested.Value.Length > 0)
        {
            findings.Add(NewFinding(AreaHardware, Sev.Problem, "Your GPU isn't being used",
                "None (everything on the CPU)",
                suggested.Label,
                $"{hw.GpuDescription} is available to Jellyfin, but hardware acceleration is off. Every transcode, plus chapter and trickplay images, is being done by the CPU.",
                WhereTranscoding));
        }
        else if (!hwOn)
        {
            findings.Add(NewFinding(AreaHardware, hw.CpuThreads <= 8 ? Sev.Improve : Sev.Tip, "No GPU available to Jellyfin",
                $"CPU only, {hw.CpuThreads} threads",
                HostPlatform.GpuSetup,
                "Without a GPU, each 1080p transcode can take several CPU threads, and 4K or HDR transcodes may stutter. If the server has integrated graphics, using it costs nothing.",
                WhereDocker));
        }
        else
        {
            // Medic can only see the GPU's devices on Linux. Elsewhere it trusts the setting and checks the rest.
            bool deviceThere = !isLinux || hwType switch
            {
                "nvenc" => hw.NvidiaDevice,
                "qsv" or "vaapi" => hw.RenderNodes.Count > 0,
                _ => true
            };

            if (!deviceThere)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Problem,
                    isContainer ? "Hardware acceleration is on, but the GPU isn't in the container" : "Hardware acceleration is on, but Jellyfin can't see the GPU",
                    Nice(hwType) ?? hwType, HostPlatform.GpuSetup + ", or set hardware acceleration to None",
                    "Jellyfin will try to use a device that isn't there, so transcodes fail or fall back to the CPU.",
                    WhereDocker));
            }
            else if (isLinux && suggested.Value.Length > 0 && hwType != suggested.Value && !(hw.GpuVendor == "intel" && hwType == "vaapi"))
            {
                findings.Add(NewFinding(AreaHardware, Sev.Tip, "Hardware acceleration type doesn't match your GPU",
                    Nice(hwType) ?? hwType, suggested.Label,
                    $"You have {hw.GpuDescription}; {suggested.Label} is the method designed for it.",
                    WhereTranscoding));
            }

            if (SettingsReader.Bool(enc, "EnableHardwareEncoding") == false)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Improve, "Hardware encoding is off", "Off", "On",
                    "The GPU decodes video but the CPU still does the heavier encoding step.", WhereTranscoding));
            }

            var codecs = SettingsReader.List(enc, "HardwareDecodingCodecs");
            if (codecs is not null && codecs.Count == 0)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Improve, "No formats are ticked for hardware decoding", "None ticked",
                    "Tick H264 and HEVC, plus VP9 and AV1 if your GPU supports them",
                    "With nothing ticked, the CPU decodes every video even though the GPU could.", WhereTranscoding));
            }
            else if (codecs is not null && deviceThere)
            {
                var missing = new[] { "h264", "hevc" }.Where(c => !codecs.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                {
                    findings.Add(NewFinding(AreaHardware, Sev.Improve, $"{string.Join(" and ", missing.Select(c => c.ToUpperInvariant()))} {(missing.Count == 1 ? "isn't" : "aren't")} decoded on the GPU",
                        "Ticked: " + string.Join(", ", codecs.Select(c => c.ToUpperInvariant())), "Tick H264 and HEVC",
                        "Nearly all films and series are H264 or HEVC. Unticked, the CPU decodes them before the GPU encodes, which is the slow half of a transcode.", WhereTranscoding));
                }
            }

            if (deviceThere && SettingsReader.Bool(enc, "EnableDecodingColorDepth10Hevc") == false)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Improve, "10-bit HEVC is decoded on the CPU", "Off", "Enable 10-bit hardware decoding for HEVC: on",
                    "Most HDR and many recent films are 10-bit HEVC. Decoding them on the CPU is heavy, and 4K HDR can stutter. Every GPU Jellyfin supports from the last several years can do it.", WhereTranscoding));
            }

            if (deviceThere && SettingsReader.Bool(enc, "AllowHevcEncoding") == false && SettingsReader.Bool(enc, "EnableHardwareEncoding") != false)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Tip, "Transcodes are only made as H264", "HEVC encoding off", "Allow encoding in HEVC format: on",
                    "Apps that can play HEVC get the same picture at about half the bitrate, which helps remote viewers on slow connections. Apps that can't still get H264.", WhereTranscoding));
            }

            if (deviceThere && hw.GpuVendor == "intel" && hwType is "qsv" or "vaapi"
                && SettingsReader.Bool(enc, "EnableIntelLowPowerH264HwEncoder") == false
                && SettingsReader.Bool(enc, "EnableIntelLowPowerHevcHwEncoder") == false)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Tip, "Intel low-power encoding is off", "Off", "Enable Intel Low-Power H.264 and HEVC encoders: on",
                    "On 8th-generation Intel and later, the low-power encoder is faster and leaves the rest of the GPU free for tone mapping and more streams. It needs the GuC/HuC firmware on the host, which most modern Linux systems load by default.", WhereTranscoding));
            }

            // Trickplay images are made with the same FFmpeg, and can use the GPU too.
            // (Trickplay on the CPU altogether is checked under Server.)
            if (SettingsReader.Bool(server, "TrickplayOptions.EnableHwAcceleration") != false
                && SettingsReader.Bool(server, "TrickplayOptions.EnableHwEncoding") == false)
            {
                findings.Add(NewFinding(AreaHardware, Sev.Tip, "Trickplay images are encoded on the CPU", "Hardware encoding for trickplay: off", "On (if your GPU supports MJPEG encoding)",
                    "Intel and recent AMD GPUs can also encode the thumbnails, which takes the last part of the work off the CPU.", WhereTrickplay));
            }
        }

        string preset = (SettingsReader.Text(enc, "EncoderPreset") ?? string.Empty).Trim().ToLowerInvariant();
        if (!hwOn && preset is "slow" or "slower" or "veryslow")
        {
            findings.Add(NewFinding(AreaHardware, Sev.Improve, "Software transcodes use a slow preset", preset, "Auto, or veryfast",
                "With no GPU doing the work, a slow preset makes each transcode use far more CPU for a small gain in quality, so fewer people can watch at once.", WhereTranscoding));
        }

        if (!hwOn && SettingsReader.Bool(server, "TrickplayOptions.EnableKeyFrameOnlyExtraction") == false)
        {
            findings.Add(NewFinding(AreaHardware, Sev.Tip, "Trickplay reads every frame", "Key frames only: off", "On",
                "Without a GPU, making trickplay images from key frames only is many times faster. The thumbnails are a little less exact.", WhereTrickplay));
        }

        bool? toneMapping = SettingsReader.Bool(enc, "EnableTonemapping");
        // QSV and VAAPI can tone map through Intel's VPP, and VideoToolbox through its own switch: either counts as on.
        // Jellyfin only uses VPP on its Intel paths, so on an AMD GPU with VAAPI that switch does nothing.
        bool ownToneMapping = hwType switch
        {
            "qsv" => SettingsReader.Bool(enc, "EnableVppTonemapping") == true,
            "vaapi" => SettingsReader.Bool(enc, "EnableVppTonemapping") == true && hw.GpuVendor != "amd",
            "videotoolbox" => SettingsReader.Bool(enc, "EnableVideoToolboxTonemapping") == true,
            _ => false
        };
        if (!hwOn && toneMapping == true)
        {
            findings.Add(NewFinding(AreaHardware, Sev.Improve, "Tone mapping is on without a GPU", "On (CPU)", "Off, or add a GPU",
                "Converting HDR to SDR on the CPU is very heavy and can make HDR transcodes unwatchable.", WhereTranscoding));
        }
        else if (hwOn && toneMapping == false && !ownToneMapping)
        {
            findings.Add(NewFinding(AreaHardware, Sev.Tip, "Tone mapping is off", "Off", "On",
                "HDR films transcoded for non-HDR screens look washed out without it, and your GPU can do it cheaply.", WhereTranscoding));
        }

        return findings;
    }

    private void CheckTranscoding(HardwareInfo hw, object? enc)
    {
        if (enc is null)
        {
            Add(AreaHardware, Sev.Tip, "Couldn't read your transcoding settings", "Unknown", string.Empty,
                "Jellyfin didn't return its transcoding configuration, so these checks were skipped.", WhereTranscoding);
            return;
        }

        _report.Findings.AddRange(AccelerationChecks(hw, enc, _config.Configuration, HostPlatform.IsLinux, HostPlatform.IsContainer));

        if (SettingsReader.Bool(enc, "EnableThrottling") == false)
        {
            Add(AreaHardware, Sev.Tip, "Transcode throttling is off", "Off", "On",
                "Throttling pauses a transcode once it's far enough ahead of playback, which saves CPU/GPU time. Turn it back off if a particular device has trouble.",
                WhereTranscoding);
        }

        string transcodePath = TranscodePath(enc);
        var mount = SystemProbe.Mount(transcodePath);
        var space = SystemProbe.Space(transcodePath);
        bool inRam = mount?.FsType == "tmpfs";

        if (SettingsReader.Bool(enc, "EnableSegmentDeletion") == false)
        {
            Add(AreaHardware, inRam ? Sev.Improve : Sev.Tip, "Old transcode segments aren't deleted", "Off", "On",
                inRam ? "Your transcode folder is in RAM, so leftover segments eat memory until the transcode ends."
                      : "Leftover segments build up on disk while long films transcode.",
                WhereTranscoding);
        }

        long? threads = SettingsReader.Number(enc, "EncodingThreadCount");
        if (threads > hw.CpuThreads)
        {
            Add(AreaHardware, Sev.Improve, "Transcoding is set to use more threads than you have",
                threads.Value.ToString(CultureInfo.InvariantCulture), "Auto (-1)",
                $"Only {hw.CpuThreads} threads are available to Jellyfin, so the extra threads just compete with each other.",
                WhereTranscoding);
        }

        string? ffmpeg = SettingsReader.Text(enc, "EncoderAppPathDisplay") ?? SettingsReader.Text(enc, "EncoderAppPath");
        if (!string.IsNullOrWhiteSpace(ffmpeg) && !File.Exists(ffmpeg))
        {
            Add(AreaHardware, Sev.Problem, "FFmpeg wasn't found", ffmpeg, "The path to Jellyfin's bundled FFmpeg",
                "Without FFmpeg, nothing can be transcoded and no images can be extracted.", WhereTranscoding);
        }

        if (inRam)
        {
            if (space is { } ram && ram.TotalGb < 2)
            {
                Add(AreaHardware, Sev.Problem, "Your transcode folder is in RAM but very small",
                    $"{SystemProbe.Gb(ram.TotalGb)} available",
                    "At least 4 GB: " + HostPlatform.BiggerRamFolder,
                    HostPlatform.IsDockerLike
                        ? "Docker gives /dev/shm only 64 MB by default. Transcodes fail part-way through when it fills up."
                        : "Transcodes fail part-way through when the RAM folder fills up.",
                    WhereDocker);
            }
        }
        else
        {
            if (mount?.FsType.StartsWith("fuse.shfs", StringComparison.Ordinal) == true)
            {
                Add(AreaHardware, Sev.Improve, "Transcodes go through Unraid's user-share layer",
                    $"{transcodePath} (on /mnt/user)", "A cache-pool path (/mnt/cache/...) or RAM",
                    "The user-share layer (FUSE) adds overhead to every write and can wake array disks.", WhereDocker);
            }

            if (space is { } disk && disk.FreeGb < 10)
            {
                Add(AreaHardware, Sev.Improve, "Low space for transcodes", $"{SystemProbe.Gb(disk.FreeGb)} free", "At least 10 GB free",
                    "A 4K transcode can use several GB of temporary space; when it runs out, playback stops.", WhereTranscoding);
            }

            if (hw.MemoryGb >= 16 && HostPlatform.RamTranscodeHow is { } how)
            {
                Add(AreaHardware, Sev.Tip, "You could transcode to RAM", transcodePath, how,
                    $"Jellyfin can use {SystemProbe.Gb(hw.MemoryGb)} of memory. Transcoding to RAM saves SSD wear and is slightly faster. This is optional: "
                        + (HostPlatform.Kind == HostKind.Unraid ? "an SSD cache pool is perfectly fine." : "an SSD is perfectly fine too."),
                    WhereDocker + ", then Dashboard → Playback → Transcoding");
            }
        }
    }
}

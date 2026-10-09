using System;
using System.Collections.Generic;
using System.Linq;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Ties findings to the Jellyfin setting they're about, so the settings list can show a red, yellow
/// or green marker beside each setting. Matched on the finding's title; findings with no setting
/// (disk space, tasks and so on) are simply left unlinked.
/// </summary>
public static class SettingLinks
{
    // (words in the title, section, setting name). "Library" means the library named before the colon.
    private static readonly (string Title, string Section, string Name)[] Map =
    {
        ("Hardware encoding is off", "Transcoding", "EnableHardwareEncoding"),
        ("Image resizing can use every CPU thread", "General", "ParallelImageEncodingLimit"),
        ("aren't decoded on the GPU", "Transcoding", "HardwareDecodingCodecs"),
        ("10-bit HEVC is decoded on the CPU", "Transcoding", "EnableDecodingColorDepth10Hevc"),
        ("Transcodes are only made as H264", "Transcoding", "AllowHevcEncoding"),
        ("Intel low-power encoding is off", "Transcoding", "EnableIntelLowPowerH264HwEncoder"),
        ("Software transcodes use a slow preset", "Transcoding", "EncoderPreset"),
        ("Trickplay images are encoded on the CPU", "General", "TrickplayOptions.EnableHwEncoding"),
        ("Trickplay reads every frame", "General", "TrickplayOptions.EnableKeyFrameOnlyExtraction"),
        ("No formats are ticked for hardware decoding", "Transcoding", "HardwareDecodingCodecs"),
        ("Tone mapping", "Transcoding", "EnableTonemapping"),
        ("Transcoding is set to use more threads", "Transcoding", "EncodingThreadCount"),
        ("Your GPU isn't being used", "Transcoding", "HardwareAccelerationType"),
        ("Hardware acceleration is off", "Transcoding", "HardwareAccelerationType"),
        ("Hardware acceleration type doesn't match", "Transcoding", "HardwareAccelerationType"),
        ("Hardware acceleration is on, but", "Transcoding", "HardwareAccelerationType"),
        ("Transcode throttling is off", "Transcoding", "EnableThrottling"),
        ("You could transcode to RAM", "Transcoding", "TranscodingTempPath"),
        ("Low space for transcodes", "Transcoding", "TranscodingTempPath"),
        ("Transcodes go through Unraid", "Transcoding", "TranscodingTempPath"),
        ("Your transcode folder is in RAM but very small", "Transcoding", "TranscodingTempPath"),
        ("remote streaming limit", "General", "RemoteClientBitrateLimit"),
        ("Remote streaming is capped", "General", "RemoteClientBitrateLimit"),
        ("better quality away from home", "General", "RemoteClientBitrateLimit"),
        ("Trickplay images are made on the CPU", "General", "TrickplayOptions.EnableHwAcceleration"),
        ("Trickplay is set to use more threads", "General", "TrickplayOptions.ProcessThreads"),
        ("Parallel library scan", "General", "LibraryScanFanoutConcurrency"),
        ("library scan tasks", "General", "LibraryScanFanoutConcurrency"),
        ("image encoding", "General", "ParallelImageEncodingLimit"),
        ("Library changes are picked up very quickly", "General", "LibraryMonitorDelay"),
        ("The activity log is kept forever", "General", "ActivityLogRetentionDays"),
        ("The server has no name", "General", "ServerName"),
        ("Remote access is on without HTTPS", "Networking", "EnableHttps"),
        ("A long TV guide is downloaded", "Live TV", "GuideDays"),
        ("chapter images are extracted during scans", "Library", "ExtractChapterImagesDuringLibraryScan"),
        ("chapter images are on for a streamed library", "Library", "EnableChapterImageExtraction"),
        ("metadata is re-downloaded every", "Library", "AutomaticRefreshIntervalDays"),
        ("trickplay images are made during scans", "Library", "ExtractTrickplayImagesDuringLibraryScan"),
        ("trickplay is on for a streamed library", "Library", "EnableTrickplayImageExtraction"),
        ("audio normalisation scanning is on", "Library", "EnableLUFSScan"),
        ("real-time monitoring", "Library", "EnableRealtimeMonitor")
    };

    public static void Attach(IEnumerable<Finding> findings)
    {
        foreach (var f in findings)
        {
            if (!string.IsNullOrEmpty(f.Setting))
            {
                continue;
            }

            var hit = Map.FirstOrDefault(m => f.Title.Contains(m.Title, StringComparison.OrdinalIgnoreCase));
            if (hit.Title is null)
            {
                continue;
            }

            string section = hit.Section;
            if (section == "Library")
            {
                int colon = f.Title.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                section = "Library: " + f.Title[..colon];
            }

            f.Setting = section + "|" + hit.Name;
        }
    }
}

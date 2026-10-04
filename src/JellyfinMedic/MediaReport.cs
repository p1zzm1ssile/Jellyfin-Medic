using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

// ---------- Models ----------

public sealed class ReportRow
{
    public string Name { get; set; } = string.Empty;

    public int Files { get; set; }

    public long Bytes { get; set; }
}

public sealed class SavingsEstimate
{
    // Files in older or less efficient formats that could be converted to HEVC.
    public int CandidateFiles { get; set; }

    public long CandidateBytes { get; set; }

    // Rough space freed with a GPU encoder (NVENC, QSV, VAAPI, AMF, VideoToolbox).
    public long GpuBytes { get; set; }

    // Rough space freed with slow software x265.
    public long SoftwareBytes { get; set; }
}

public sealed class BigFile
{
    public string Title { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public string Resolution { get; set; } = string.Empty;

    public double? Mbps { get; set; }

    public long Bytes { get; set; }

    public long GpuSavingBytes { get; set; }
}

public sealed class CompatibilityCounts
{
    // Every audio track is DTS or TrueHD, which browsers and many TVs can't play.
    public int DtsOrTrueHdOnly { get; set; }

    // Has picture-based subtitles (PGS, VobSub), which must be burned in when shown.
    public int ImageSubtitles { get; set; }

    // AVI, WMV, MPEG and similar older file types.
    public int OldContainers { get; set; }

    // HDR10, HDR10+, Dolby Vision or HLG; needs tone mapping on non-HDR screens.
    public int Hdr { get; set; }
}

public sealed class MediaReportResult
{
    public DateTime GeneratedUtc { get; set; }

    public int Files { get; set; }

    public long Bytes { get; set; }

    // Hardware acceleration set in Jellyfin when the report was built, e.g. "nvenc".
    public string HardwareAcceleration { get; set; } = "none";

    public List<ReportRow> Codecs { get; set; } = new();

    public List<ReportRow> Resolutions { get; set; } = new();

    public List<ReportRow> DynamicRange { get; set; } = new();

    public List<ReportRow> Containers { get; set; } = new();

    public SavingsEstimate Savings { get; set; } = new();

    public List<BigFile> BiggestSavings { get; set; } = new();

    public CompatibilityCounts Compatibility { get; set; } = new();
}

public sealed class MediaReportStatus
{
    public bool Running { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public MediaReportResult? Report { get; set; }

    public TranscodeLogView Transcodes { get; set; } = new();
}

// ---------- Builder ----------

/// <summary>
/// A read-only look at the library: formats, sizes, what converting could save, and which files are
/// likely to make Jellyfin transcode. It reads what Jellyfin already knows about each file (no rescans,
/// no FFmpeg) and never changes anything.
/// </summary>
public static class MediaReport
{
    // Share of a file's size that converting to HEVC typically frees. Deliberately conservative:
    // GPU encoders give bigger files than slow software x265 at the same quality.
    private static readonly Dictionary<string, (double Gpu, double Software)> Savings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = (0.30, 0.45),
        ["mpeg2video"] = (0.50, 0.60),
        ["vc1"] = (0.50, 0.60),
        ["mpeg4"] = (0.45, 0.55),
        ["msmpeg4v3"] = (0.45, 0.55),
        ["msmpeg4v2"] = (0.45, 0.55),
        ["wmv3"] = (0.45, 0.55),
        ["wmv2"] = (0.45, 0.55),
        ["h263"] = (0.45, 0.55),
        ["mpeg1video"] = (0.50, 0.60)
    };

    private static readonly HashSet<string> ImageSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pgssub", "hdmv_pgs_subtitle", "pgs", "dvdsub", "dvd_subtitle", "vobsub", "dvbsub", "dvb_subtitle"
    };

    private static readonly HashSet<string> OldContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".wmv", ".mpg", ".mpeg", ".divx", ".flv", ".vob", ".ogm", ".rmvb", ".rm", ".asf"
    };

    private static readonly object Sync = new();
    private static bool _running;
    private static int _done;
    private static int _total;
    private static MediaReportResult? _cached;

    public static MediaReportStatus Status(IApplicationPaths paths)
    {
        lock (Sync)
        {
            _cached ??= LoadSaved(paths);
            return new MediaReportStatus
            {
                Running = _running,
                Done = _done,
                Total = _total,
                Report = _cached,
                Transcodes = TranscodeLog.View(paths)
            };
        }
    }

    /// <summary>Starts building in the background. Returns false if a build is already running.</summary>
    public static bool Start(ILibraryManager library, IEnumerable<LibraryFacts> libraries, IApplicationPaths paths, string hardwareAcceleration)
    {
        lock (Sync)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _done = 0;
            _total = 0;
        }

        var libs = libraries.Where(l => !l.IsStreamed).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                var result = Build(library, libs, hardwareAcceleration);
                Save(paths, result);
                lock (Sync) { _cached = result; }
            }
            catch
            {
                // Leave the previous report in place.
            }
            finally
            {
                lock (Sync) { _running = false; }
            }
        });
        return true;
    }

    private static MediaReportResult Build(ILibraryManager library, List<LibraryFacts> libraries, string hardwareAcceleration)
    {
        var items = new List<BaseItem>();
        foreach (var lib in libraries)
        {
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            try
            {
                items.AddRange(library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }));
            }
            catch
            {
                // Skip a library that can't be read.
            }
        }

        lock (Sync) { _total = items.Count; }

        var report = new MediaReportResult { GeneratedUtc = DateTime.UtcNow, HardwareAcceleration = hardwareAcceleration };
        var codecs = new Dictionary<string, ReportRow>(StringComparer.OrdinalIgnoreCase);
        var resolutions = new Dictionary<string, ReportRow>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, ReportRow>(StringComparer.Ordinal);
        var containers = new Dictionary<string, ReportRow>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<BigFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            lock (Sync) { _done++; }

            string path = item.Path ?? string.Empty;
            if (string.IsNullOrEmpty(path) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !LooksLocal(path) || !seenPaths.Add(path))
            {
                continue;
            }

            var streams = ReadStreams(item);
            var video = streams.FirstOrDefault(s => Is(s, "Type", "Video"));
            if (video is null)
            {
                continue; // music, books, photos
            }

            long bytes = SettingsReader.Number(item, "Size") is { } size && size > 0 ? size : FileSize(path);
            string codec = Normalise((SettingsReader.Text(video, "Codec") ?? "unknown").ToLowerInvariant());
            int width = (int)(SettingsReader.Number(video, "Width") ?? 0);
            int height = (int)(SettingsReader.Number(video, "Height") ?? 0);
            string resolution = ResolutionLabel(width, height);
            string range = RangeLabel(SettingsReader.Text(video, "VideoRangeType"), SettingsReader.Text(video, "VideoRange"));
            string ext = Path.GetExtension(path).ToLowerInvariant();

            report.Files++;
            report.Bytes += bytes;
            Bump(codecs, CodecLabel(codec), bytes);
            Bump(resolutions, resolution, bytes);
            Bump(ranges, range, bytes);
            Bump(containers, ext.Length > 1 ? ext.TrimStart('.').ToUpperInvariant() : "Other", bytes);

            // Compatibility signals
            var audio = streams.Where(s => Is(s, "Type", "Audio")).ToList();
            if (audio.Count > 0 && audio.All(a => (SettingsReader.Text(a, "Codec") ?? string.Empty).ToLowerInvariant() is "dts" or "truehd" or "mlp"))
            {
                report.Compatibility.DtsOrTrueHdOnly++;
            }

            if (streams.Any(s => Is(s, "Type", "Subtitle") && ImageSubtitleCodecs.Contains(SettingsReader.Text(s, "Codec") ?? string.Empty)))
            {
                report.Compatibility.ImageSubtitles++;
            }

            if (OldContainers.Contains(ext))
            {
                report.Compatibility.OldContainers++;
            }

            if (range != "SDR" && range != "Unknown")
            {
                report.Compatibility.Hdr++;
            }

            // Savings from converting to HEVC
            if (Savings.TryGetValue(codec, out var factor) && bytes > 0)
            {
                long gpu = (long)(bytes * factor.Gpu);
                long software = (long)(bytes * factor.Software);
                report.Savings.CandidateFiles++;
                report.Savings.CandidateBytes += bytes;
                report.Savings.GpuBytes += gpu;
                report.Savings.SoftwareBytes += software;

                double? seconds = SettingsReader.Number(item, "RunTimeTicks") is { } ticks && ticks > 0 ? ticks / 10_000_000d : null;
                candidates.Add(new BigFile
                {
                    Title = Title(item),
                    FileName = Path.GetFileName(path),
                    Codec = CodecLabel(codec),
                    Resolution = resolution,
                    Mbps = seconds is { } sec && sec > 60 ? Math.Round(bytes * 8 / sec / 1_000_000d, 1) : null,
                    Bytes = bytes,
                    GpuSavingBytes = gpu
                });
            }
        }

        report.Codecs = Sorted(codecs);
        report.Resolutions = resolutions.Values.OrderBy(r => ResolutionOrder(r.Name)).ToList();
        report.DynamicRange = Sorted(ranges);
        report.Containers = Sorted(containers);
        report.BiggestSavings = candidates.OrderByDescending(c => c.GpuSavingBytes).Take(25).ToList();
        return report;
    }

    // ---------- Helpers ----------

    private static void Bump(Dictionary<string, ReportRow> rows, string name, long bytes)
    {
        if (!rows.TryGetValue(name, out var row))
        {
            row = new ReportRow { Name = name };
            rows[name] = row;
        }

        row.Files++;
        row.Bytes += bytes;
    }

    private static List<ReportRow> Sorted(Dictionary<string, ReportRow> rows) =>
        rows.Values.OrderByDescending(r => r.Bytes).ToList();

    private static string Normalise(string codec) => codec switch
    {
        "avc" or "avc1" or "x264" => "h264",
        "h265" or "hevc" or "x265" => "hevc",
        "divx" or "xvid" or "mp4v" => "mpeg4",
        _ => codec
    };

    private static string CodecLabel(string codec) => codec switch
    {
        "h264" => "H.264 (AVC)",
        "hevc" => "HEVC (H.265)",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" => "MPEG-2",
        "mpeg1video" => "MPEG-1",
        "vc1" => "VC-1",
        "mpeg4" => "MPEG-4 (DivX/Xvid)",
        "msmpeg4v2" or "msmpeg4v3" => "MS MPEG-4",
        "wmv2" or "wmv3" => "WMV",
        "h263" => "H.263",
        _ => codec.ToUpperInvariant()
    };

    private static string ResolutionLabel(int width, int height)
    {
        if (width <= 0 && height <= 0)
        {
            return "Unknown";
        }

        if (width >= 3200 || height >= 1800)
        {
            return "4K";
        }

        if (width >= 1700 || height >= 1000)
        {
            return "1080p";
        }

        if (width >= 1200 || height >= 700)
        {
            return "720p";
        }

        return "SD";
    }

    private static int ResolutionOrder(string label) => label switch
    {
        "4K" => 0,
        "1080p" => 1,
        "720p" => 2,
        "SD" => 3,
        _ => 4
    };

    private static string RangeLabel(string? rangeType, string? range)
    {
        string t = (rangeType ?? string.Empty).ToUpperInvariant();
        if (t.StartsWith("DOVI", StringComparison.Ordinal))
        {
            return "Dolby Vision";
        }

        if (t.Contains("HDR10PLUS", StringComparison.Ordinal))
        {
            return "HDR10+";
        }

        if (t.Contains("HDR10", StringComparison.Ordinal))
        {
            return "HDR10";
        }

        if (t.Contains("HLG", StringComparison.Ordinal))
        {
            return "HLG";
        }

        if (t == "SDR")
        {
            return "SDR";
        }

        string r = (range ?? string.Empty).ToUpperInvariant();
        return r == "HDR" ? "HDR (other)" : r == "SDR" ? "SDR" : "Unknown";
    }

    private static string Title(BaseItem item)
    {
        string? series = SettingsReader.Text(item, "SeriesName");
        long? season = SettingsReader.Number(item, "ParentIndexNumber");
        long? episode = SettingsReader.Number(item, "IndexNumber");
        if (!string.IsNullOrWhiteSpace(series) && season is not null && episode is not null)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} S{1:00}E{2:00}", series, season, episode);
        }

        long? year = SettingsReader.Number(item, "ProductionYear");
        return year is { } y ? $"{item.Name} ({y})" : item.Name ?? string.Empty;
    }

    private static bool Is(object stream, string property, string value) =>
        string.Equals(SettingsReader.Text(stream, property), value, StringComparison.OrdinalIgnoreCase);

    private static List<object> ReadStreams(BaseItem item)
    {
        try
        {
            var method = item.GetType().GetMethod("GetMediaStreams", Type.EmptyTypes);
            if (method?.Invoke(item, null) is System.Collections.IEnumerable list)
            {
                return list.Cast<object>().ToList();
            }
        }
        catch
        {
            // No streams for this item.
        }

        return new List<object>();
    }

    private static bool LooksLocal(string path) =>
        path.StartsWith('/') || (path.Length > 2 && path[1] == ':');

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ---------- Saved copy, so the tab opens with the last report ----------

    private static string FilePath(IApplicationPaths paths) =>
        Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "media_report.json");

    private static MediaReportResult? LoadSaved(IApplicationPaths paths)
    {
        try
        {
            string file = FilePath(paths);
            return File.Exists(file) ? JsonSerializer.Deserialize<MediaReportResult>(File.ReadAllText(file)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(IApplicationPaths paths, MediaReportResult report)
    {
        try
        {
            string file = FilePath(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(report));
        }
        catch
        {
            // Keeping it in memory is enough.
        }
    }
}

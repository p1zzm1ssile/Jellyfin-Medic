using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;

namespace JellyfinMedic.Services;

// ---------- Results ----------

public class DecodeRun
{
    public string Method { get; set; } = string.Empty;

    public bool Worked { get; set; }

    // How many times faster than real time, e.g. 350 means a 2-hour film is scanned in about 20 seconds.
    public double? Speed { get; set; }

    public double? SecondsPerTwoHourFilm { get; set; }

    public string? Error { get; set; }
}

public class DecodeTestResult
{
    public string File { get; set; } = string.Empty;

    public string ConfiguredAcceleration { get; set; } = "none";

    public List<DecodeRun> Runs { get; set; } = new();
}

public class TranscodeLogResult
{
    public bool Found { get; set; }

    public string? FileName { get; set; }

    // Transcode, Remux or DirectStream (from the log's file name)
    public string? Kind { get; set; }

    public string ConfiguredAcceleration { get; set; } = "none";

    public string Decoding { get; set; } = "Unknown";

    public bool DecodingOnGpu { get; set; }

    public string Encoding { get; set; } = "Unknown";

    public bool EncodingOnGpu { get; set; }

    public string ToneMapping { get; set; } = "Not used";

    public double? FfmpegSpeed { get; set; }

    public List<string> Errors { get; set; } = new();
}

public class SpeedOption
{
    public int Viewers { get; set; }

    public double PerViewerMbps { get; set; }

    // 0 means no limit is needed.
    public int RecommendedLimitMbps { get; set; }

    public string Quality { get; set; } = string.Empty;
}

public class SpeedTestResult
{
    public DateTime TestedUtc { get; set; } = DateTime.UtcNow;

    public double DownloadMbps { get; set; }

    public double UploadMbps { get; set; }

    public double LatencyMs { get; set; }

    public long CurrentLimitBps { get; set; }

    // Roughly how many HD IPTV channels the download speed can carry at once.
    public int IptvHdStreams { get; set; }

    public List<SpeedOption> Options { get; set; } = new();

    public string? Error { get; set; }
}

// ---------- Image extraction (trickplay / chapter images) ----------

/// <summary>
/// Times how fast FFmpeg can scan through a film's keyframes, which is the bulk of the work
/// for trickplay and chapter images. Runs once on the GPU (if configured) and once on the CPU.
/// Output goes nowhere: no files are written.
/// </summary>
public static class DecodeTest
{
    public static async Task<DecodeTestResult> RunAsync(string ffmpeg, string file, string hwType, string? vaapiDevice, CancellationToken ct)
    {
        var result = new DecodeTestResult { File = Path.GetFileName(file), ConfiguredAcceleration = hwType };

        string device = string.IsNullOrWhiteSpace(vaapiDevice) ? "/dev/dri/renderD128" : vaapiDevice;
        (string[] Args, string Label)? gpu = hwType switch
        {
            "nvenc" => (new[] { "-hwaccel", "cuda" }, "GPU (NVIDIA CUDA)"),
            "qsv" or "vaapi" => (new[] { "-hwaccel", "vaapi", "-hwaccel_device", device }, "GPU (VAAPI)"),
            _ => null
        };

        if (gpu is { } g)
        {
            result.Runs.Add(await RunOnceAsync(ffmpeg, file, g.Args, g.Label, ct).ConfigureAwait(false));
        }

        result.Runs.Add(await RunOnceAsync(ffmpeg, file, Array.Empty<string>(), "CPU", ct).ConfigureAwait(false));
        return result;
    }

    private static async Task<DecodeRun> RunOnceAsync(string ffmpeg, string file, string[] hwArgs, string method, CancellationToken ct)
    {
        var run = new DecodeRun { Method = method };
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error" })
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var arg in hwArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        // Keyframes only, first 20 minutes, video only, output discarded.
        foreach (var arg in new[] { "-skip_frame", "nokey", "-t", "1200", "-i", file, "-map", "0:v:0", "-an", "-sn", "-dn", "-f", "null", "-progress", "pipe:1", "-nostats", "-" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        string? lastSpeed = null;
        bool timedOut = false;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            run.Error = $"Couldn't start FFmpeg: {ex.Message}";
            return run;
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("speed=", StringComparison.Ordinal) && !line.Contains("N/A", StringComparison.Ordinal))
                {
                    lastSpeed = line[6..].Trim().TrimEnd('x');
                }
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Already gone.
            }
        }

        await Task.WhenAny(stderrTask, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
        string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

        if (double.TryParse(lastSpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
        {
            run.Speed = Math.Round(speed, 1);
            run.SecondsPerTwoHourFilm = Math.Round(7200 / speed, 0);
        }

        bool exitedOk = !timedOut && process.HasExited && process.ExitCode == 0;
        run.Worked = exitedOk || (timedOut && run.Speed is not null);
        if (!run.Worked)
        {
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            run.Error = lines.Length == 0 ? "FFmpeg stopped without an error message." : string.Join(" ", lines.TakeLast(3));
        }

        return run;
    }
}

// ---------- Reading Jellyfin's FFmpeg log after a playback test ----------

public static class TranscodeLogReader
{
    private static readonly Regex HwAccel = new(@"-hwaccel\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex VideoEncoder = new(@"-(?:codec|c):v(?::0)?\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex SpeedValue = new(@"speed=\s*([\d.]+)x", RegexOptions.Compiled);

    public static TranscodeLogResult Read(string logDir, DateTime sinceUtc, string? match, string configuredHw)
    {
        var result = new TranscodeLogResult { ConfiguredAcceleration = configuredHw };

        FileInfo? log;
        try
        {
            log = new DirectoryInfo(logDir)
                .GetFiles("FFmpeg.*.log")
                .Where(f => f.LastWriteTimeUtc >= sinceUtc.ToUniversalTime().AddSeconds(-10))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault(f => string.IsNullOrEmpty(match) || ReadText(f.FullName).Contains(match, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return result;
        }

        if (log is null)
        {
            return result;
        }

        string text = ReadText(log.FullName);
        result.Found = true;
        result.FileName = log.Name;
        result.Kind = log.Name.StartsWith("FFmpeg.Transcode", StringComparison.OrdinalIgnoreCase) ? "Transcode"
                    : log.Name.StartsWith("FFmpeg.Remux", StringComparison.OrdinalIgnoreCase) ? "Remux"
                    : log.Name.StartsWith("FFmpeg.DirectStream", StringComparison.OrdinalIgnoreCase) ? "DirectStream"
                    : "Unknown";

        var hw = HwAccel.Match(text);
        result.DecodingOnGpu = hw.Success;
        result.Decoding = hw.Success ? $"GPU ({hw.Groups[1].Value})" : "CPU";

        var encoder = VideoEncoder.Matches(text).Select(m => m.Groups[1].Value).LastOrDefault();
        if (encoder is not null)
        {
            result.EncodingOnGpu = Regex.IsMatch(encoder, "nvenc|qsv|vaapi|amf|v4l2m2m|rkmpp|videotoolbox");
            result.Encoding = encoder == "copy" ? "Not re-encoded (copied)" : $"{encoder} ({(result.EncodingOnGpu ? "GPU" : "CPU")})";
        }

        result.ToneMapping = text.Contains("tonemap_cuda", StringComparison.Ordinal) ? "GPU (CUDA)"
                           : text.Contains("tonemap_opencl", StringComparison.Ordinal) ? "GPU (OpenCL)"
                           : text.Contains("tonemap_vaapi", StringComparison.Ordinal) ? "GPU (VAAPI)"
                           : Regex.IsMatch(text, @"vpp_qsv=[^ ]*tonemap") ? "GPU (QSV)"
                           : text.Contains("tonemapx", StringComparison.Ordinal) ? "CPU (tonemapx)"
                           : text.Contains("zscale", StringComparison.Ordinal) && text.Contains("tonemap", StringComparison.Ordinal) ? "CPU"
                           : "Not used";

        var speeds = SpeedValue.Matches(text);
        if (speeds.Count > 0 && double.TryParse(speeds[^1].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            result.FfmpegSpeed = Math.Round(speed, 2);
        }

        result.Errors = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && (l.Contains("Error", StringComparison.Ordinal) || l.Contains("error while", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase) || l.Contains("Impossible to convert", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("Failed", StringComparison.Ordinal)))
            .Select(l => l.Length > 220 ? l[..220] + "…" : l)
            .Distinct()
            .Take(8)
            .ToList();

        return result;
    }

    private static string ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new char[2_000_000];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch
        {
            return string.Empty;
        }
    }
}

// ---------- Internet speed test ----------

/// <summary>
/// Measures the server's own internet connection using Cloudflare's public speed-test service
/// (the one behind speed.cloudflare.com). Each direction is limited to about 8 seconds and a
/// capped amount of data, so it's quick even on slow connections and doesn't use gigabytes on fast ones.
/// </summary>
public static class SpeedTester
{
    private const string Base = "https://speed.cloudflare.com";
    private const int Workers = 4;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<SpeedTestResult> RunAsync(long currentLimitBps, CancellationToken ct)
    {
        var result = new SpeedTestResult { CurrentLimitBps = currentLimitBps };
        try
        {
            result.LatencyMs = await LatencyAsync(ct).ConfigureAwait(false);
            result.DownloadMbps = await DownloadAsync(ct).ConfigureAwait(false);
            result.UploadMbps = await UploadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            result.Error = $"Couldn't reach the speed test server ({ex.Message}).";
        }

        result.Options = SpeedAdvice.Options(result.UploadMbps);
        result.IptvHdStreams = (int)Math.Floor(result.DownloadMbps * 0.8 / 8);
        return result;
    }

    private static async Task<double> LatencyAsync(CancellationToken ct)
    {
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await Http.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return Math.Round(times[times.Count / 2], 0);
    }

    private static async Task<double> DownloadAsync(CancellationToken ct)
    {
        const long maxTotal = 300L * 1024 * 1024;
        long total = 0;
        var sw = Stopwatch.StartNew();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(Window);

        async Task Worker()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!window.IsCancellationRequested && Interlocked.Read(ref total) < maxTotal)
                {
                    using var response = await Http.GetAsync($"{Base}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, window.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(window.Token).ConfigureAwait(false);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, window.Token).ConfigureAwait(false)) > 0)
                    {
                        if (Interlocked.Add(ref total, read) >= maxTotal)
                        {
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Time's up: keep what was measured.
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Worker())).ConfigureAwait(false);
        sw.Stop();
        return Mbps(total, sw.Elapsed);
    }

    private static async Task<double> UploadAsync(CancellationToken ct)
    {
        const long maxTotal = 150L * 1024 * 1024;
        const int chunk = 2 * 1024 * 1024;
        var payload = new byte[chunk];
        Random.Shared.NextBytes(payload);

        long total = 0;
        var sw = Stopwatch.StartNew();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(Window);

        async Task Worker()
        {
            try
            {
                while (!window.IsCancellationRequested && Interlocked.Read(ref total) < maxTotal)
                {
                    using var content = new ByteArrayContent(payload);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var response = await Http.PostAsync($"{Base}/__up", content, window.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    Interlocked.Add(ref total, chunk);
                }
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Time's up: only fully sent chunks are counted.
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Worker())).ConfigureAwait(false);
        sw.Stop();
        return Mbps(total, sw.Elapsed);
    }

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : Math.Round(bytes * 8 / elapsed.TotalSeconds / 1_000_000, 1);
}

public static class SpeedAdvice
{
    /// <summary>
    /// For 1 to 6 people watching away from home at once: how much of the upload each gets
    /// (keeping 20% spare), the bitrate limit to set, and what quality that allows.
    /// </summary>
    public static List<SpeedOption> Options(double uploadMbps) =>
        Enumerable.Range(1, 6).Select(viewers =>
        {
            double per = Math.Round(uploadMbps * 0.8 / viewers, 1);
            return new SpeedOption
            {
                Viewers = viewers,
                PerViewerMbps = per,
                RecommendedLimitMbps = per >= 100 ? 0 : (int)Math.Max(1, Math.Floor(per)),
                Quality = Quality(per)
            };
        }).ToList();

    public static string Quality(double perViewerMbps) => perViewerMbps switch
    {
        >= 60 => "Full quality, including most 4K films without transcoding",
        >= 25 => "Full-quality 1080p; 4K films get transcoded",
        >= 12 => "Good 1080p; very high-bitrate films get transcoded",
        >= 6 => "720p to light 1080p, with most films transcoded",
        >= 3 => "720p at modest quality",
        > 0 => "Low quality (480p); remote viewing will struggle",
        _ => "Unknown"
    };
}

public static class SpeedStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FilePath(IApplicationPaths paths) =>
        Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "speedtest.json");

    public static SpeedTestResult? Load(IApplicationPaths paths)
    {
        try
        {
            string path = FilePath(paths);
            return File.Exists(path) ? JsonSerializer.Deserialize<SpeedTestResult>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(IApplicationPaths paths, SpeedTestResult result)
    {
        string path = FilePath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JellyfinMedic.Api.ScheduleStorage.WriteText(path, JsonSerializer.Serialize(result, Indented));
    }
}

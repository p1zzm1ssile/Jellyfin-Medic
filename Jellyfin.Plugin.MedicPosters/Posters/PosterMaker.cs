using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>What one poster or library cover looks like, built from the settings.</summary>
public sealed record PosterSpec(
    int Width,
    int Height,
    int Moments,
    double MomentSeconds,
    int FramesPerSecond,
    int Colours,
    bool ShowTitle,
    bool ShowFrame)
{
    /// <summary>The title font.</summary>
    public PosterFont Font { get; init; } = PosterFonts.Default;

    /// <summary>The title's size against the size that fits it: 1 is the size from before 1.3.</summary>
    public double TextScale { get; init; } = 1;

    /// <summary>Where the title sits: "top", "middle" or "bottom".</summary>
    public string TextPosition { get; init; } = "bottom";

    public bool TextShadow { get; init; } = true;

    public bool DarkBand { get; init; } = true;

    public bool FadeBetween { get; init; }

    public bool FadeEnd { get; init; }

    public double FadeSeconds { get; init; } = 0.3;

    public static readonly string[] Positions = { "top", "middle", "bottom" };

    /// <summary>The settings as a 2:3 poster spec, with every value kept in a range that makes a sensible GIF.</summary>
    public static PosterSpec From(Configuration.PluginConfiguration c)
    {
        int width = Math.Clamp(c.Width / 2 * 2, 120, 600);
        return new PosterSpec(width, width * 3 / 2, Math.Clamp(c.Moments, 1, 6), Math.Clamp(c.MomentTenths, 5, 30) / 10.0,
            Math.Clamp(c.FramesPerSecond, 4, 15), Math.Clamp(c.Colours, 16, 256), c.ShowTitle, c.ShowFrame)
            .Styled(c.Font, c.TextSize, c.TextPosition, c.TextShadow, c.DarkBand, c.FadeBetween, c.FadeEnd, c.FadeTenths);
    }

    /// <summary>The settings as a 16:9 library cover spec, the shape of Jellyfin's library cards.</summary>
    public static PosterSpec CoverFrom(Configuration.PluginConfiguration c)
    {
        int width = Math.Clamp(c.CoverWidth / 2 * 2, 240, 960);
        return new PosterSpec(width, width * 9 / 16 / 2 * 2, Math.Clamp(c.CoverMoments, 1, 6), Math.Clamp(c.CoverMomentTenths, 5, 30) / 10.0,
            Math.Clamp(c.CoverFramesPerSecond, 4, 15), Math.Clamp(c.CoverColours, 16, 256), c.CoverShowTitle, c.CoverShowFrame)
            .Styled(c.CoverFont, c.CoverTextSize, c.CoverTextPosition, c.CoverTextShadow, c.CoverDarkBand, c.CoverFadeBetween, c.CoverFadeEnd, c.CoverFadeTenths);
    }

    private PosterSpec Styled(string? font, int textSize, string? position, bool shadow, bool band, bool fadeBetween, bool fadeEnd, int fadeTenths) => this with
    {
        Font = PosterFonts.Find(font),
        TextScale = Math.Clamp(textSize is 0 ? 100 : textSize, 50, 200) / 100.0,
        TextPosition = Array.IndexOf(Positions, position) >= 0 ? position! : "bottom",
        TextShadow = shadow,
        DarkBand = band,
        FadeBetween = fadeBetween,
        FadeEnd = fadeEnd,
        // A fade never takes more than half a moment, so every moment still shows clearly.
        FadeSeconds = Math.Min(Math.Clamp(fadeTenths is 0 ? 3 : fadeTenths, 1, 10) / 10.0, MomentSeconds / 2)
    };

    /// <summary>How many frames a poster or cover made with this spec has.</summary>
    public int Frames => (int)Math.Round(Moments * MomentSeconds * FramesPerSecond);

    /// <summary>
    /// Roughly how big a GIF made with this spec is, from bytes per pixel per frame: about 0.04 on the test
    /// clips, and more for films with a lot of detail, so the page uses what's been made already when it can.
    /// </summary>
    public long EstimateBytes(double bytesPerPixelFrame) => (long)(Width * (double)Height * Frames * bytesPerPixelFrame);
}

/// <summary>One moment of a poster: which video, where in it to start, and whether it's HDR.</summary>
public sealed record Clip(string Path, double Start, bool Hdr);

/// <summary>
/// Makes an animated GIF poster with Jellyfin's own FFmpeg: a short clip from a few moments of the
/// film, cropped to a 2:3 poster, with the title and frame drawn on by libass (which Jellyfin's
/// FFmpeg always has, for burning in subtitles). GIF because Jellyfin never resizes a GIF, so it
/// stays animated in every app that can play one; other formats come back as a still picture.
/// </summary>
public class PosterMaker
{
    private const string HdrToSdr = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<PosterMaker> _logger;

    public PosterMaker(IMediaEncoder mediaEncoder, ILogger<PosterMaker> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Makes a poster from moments of one video and writes it to <paramref name="output"/>. Returns null when it
    /// worked, otherwise a short reason, which is shown on the settings page.
    /// </summary>
    public Task<string?> MakeAsync(string videoPath, string title, double? runtimeSeconds, bool hdr, PosterSpec spec, string output, CancellationToken ct) =>
        MakeAsync(MomentStarts(runtimeSeconds, spec.Moments, spec.MomentSeconds).Select(s => new Clip(videoPath, s, hdr)).ToList(), title, spec, output, ct);

    /// <summary>Makes a poster or cover from the given moments, which may come from different videos.</summary>
    public async Task<string?> MakeAsync(IReadOnlyList<Clip> clips, string title, PosterSpec spec, string output, CancellationToken ct)
    {
        string? ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
        {
            return "Jellyfin's FFmpeg wasn't found. Check Dashboard → Playback → Transcoding → FFmpeg path.";
        }

        // FFmpeg runs inside its own folder holding the drawing and the font, so the filter graph only
        // names them by short relative paths and no folder name (C:\, spaces, quotes) needs escaping.
        string work = Path.Combine(Path.GetTempPath(), "medic-posters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "fonts"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "poster.ass"), BuildAss(spec, title), new UTF8Encoding(false), ct).ConfigureAwait(false);
            await WriteFontAsync(spec.Font.File, Path.Combine(work, "fonts", spec.Font.File), ct).ConfigureAwait(false);
            string temp = Path.Combine(work, "poster.gif");

            var (exit, error) = await RunAsync(ffmpeg, work, BuildArgs(clips, spec, temp), ct).ConfigureAwait(false);
            if (exit != 0 && clips.Any(c => c.Hdr))
            {
                // An FFmpeg without HDR conversion: a washed-out poster beats no poster.
                _logger.LogInformation("Medic Posters: HDR conversion failed for {Title}, trying without it", title);
                (exit, error) = await RunAsync(ffmpeg, work, BuildArgs(clips.Select(c => c with { Hdr = false }).ToList(), spec, temp), ct).ConfigureAwait(false);
            }

            if (exit != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                _logger.LogWarning("Medic Posters: FFmpeg couldn't make a poster for {Title} from {Paths} (exit {Exit}): {Error}", title, string.Join(", ", clips.Select(c => c.Path).Distinct()), exit, error);
                return exit == -1 ? "FFmpeg took too long and was stopped." : "FFmpeg couldn't read the video: " + LastLine(error);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Move(temp, output, overwrite: true);
            return null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// One still frame of a video as a JPEG, for choosing a moment on the settings page. Null when FFmpeg
    /// couldn't read it.
    /// </summary>
    public async Task<byte[]?> FrameAsync(Clip clip, int width, CancellationToken ct)
    {
        string? ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
        {
            return null;
        }

        string work = Path.Combine(Path.GetTempPath(), "medic-posters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (exit, _) = await RunAsync(ffmpeg, work, FrameArgs(clip, width, "frame.jpg"), ct).ConfigureAwait(false);
            if (exit != 0 && clip.Hdr)
            {
                (exit, _) = await RunAsync(ffmpeg, work, FrameArgs(clip with { Hdr = false }, width, "frame.jpg"), ct).ConfigureAwait(false);
            }

            string frame = Path.Combine(work, "frame.jpg");
            return exit == 0 && File.Exists(frame) ? await File.ReadAllBytesAsync(frame, ct).ConfigureAwait(false) : null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>FFmpeg's arguments for one still frame at the clip's start.</summary>
    public static IReadOnlyList<string> FrameArgs(Clip clip, int width, string output)
    {
        string toSdr = clip.Hdr ? HdrToSdr : string.Empty;
        return new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-ss", Num(clip.Start), "-i", clip.Path,
            "-frames:v", "1", "-vf", $"{toSdr}scale={width}:-2", "-q:v", "4", "-an", "-sn", output
        };
    }

    /// <summary>
    /// Where each moment starts: spread between 15% and 75% of the film, past the opening titles and
    /// before the credits. A film of unknown length starts at its beginning.
    /// </summary>
    public static IReadOnlyList<double> MomentStarts(double? runtimeSeconds, int moments, double momentSeconds)
    {
        double length = runtimeSeconds is > 0 ? runtimeSeconds.Value : 0;
        var starts = new List<double>(moments);
        for (int i = 0; i < moments; i++)
        {
            double at;
            if (length <= momentSeconds * moments)
            {
                // Too short to spread out (or length unknown): play it in order from the start.
                at = i * momentSeconds;
                if (length > 0)
                {
                    at = Math.Min(at, Math.Max(0, length - momentSeconds));
                }
            }
            else
            {
                double fraction = moments == 1 ? 0.4 : 0.15 + (0.6 * i / (moments - 1));
                at = Math.Min(length * fraction, length - momentSeconds);
            }

            starts.Add(Math.Round(Math.Max(0, at), 2));
        }

        return starts;
    }

    /// <summary>
    /// A random moment between 15% and 75% of a video, for posters and covers that take one moment from each of
    /// several videos. A video of unknown length, or too short, starts at its beginning.
    /// </summary>
    public static double RandomStart(double? runtimeSeconds, double momentSeconds, Random random)
    {
        double length = runtimeSeconds is > 0 ? runtimeSeconds.Value : 0;
        if (length <= momentSeconds * 3)
        {
            return 0;
        }

        return Math.Round(Math.Min(length * (0.15 + (0.6 * random.NextDouble())), length - momentSeconds), 2);
    }

    /// <summary>FFmpeg's arguments for moments of one video.</summary>
    public static IReadOnlyList<string> BuildArgs(string videoPath, IReadOnlyList<double> starts, PosterSpec spec, bool hdr, string output) =>
        BuildArgs(starts.Select(s => new Clip(videoPath, s, hdr)).ToList(), spec, output);

    /// <summary>FFmpeg's arguments: one fast seek per moment, each cropped to the poster, joined, drawn on, and turned into a GIF.</summary>
    public static IReadOnlyList<string> BuildArgs(IReadOnlyList<Clip> clips, PosterSpec spec, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        foreach (var clip in clips)
        {
            args.AddRange(new[] { "-ss", Num(clip.Start), "-t", Num(spec.MomentSeconds), "-i", clip.Path });
        }

        var graph = new StringBuilder();
        for (int i = 0; i < clips.Count; i++)
        {
            string toSdr = clips[i].Hdr ? HdrToSdr : string.Empty;
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]{toSdr}fps={spec.FramesPerSecond},scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=increase,crop={spec.Width}:{spec.Height},setsar=1,format=yuv420p{Fades(spec, i, clips.Count)}[m{i}];");
        }

        for (int i = 0; i < clips.Count; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[m{i}]");
        }

        graph.Append(CultureInfo.InvariantCulture, $"concat=n={clips.Count}:v=1:a=0,");
        graph.Append("ass=filename=poster.ass:fontsdir=fonts,");
        graph.Append(CultureInfo.InvariantCulture, $"split[a][b];[a]palettegen=max_colors={spec.Colours}:stats_mode=full[p];[b][p]paletteuse=dither=bayer:bayer_scale=3");

        args.AddRange(new[] { "-filter_complex", graph.ToString(), "-an", "-sn", "-loop", "0", "-f", "gif", output });
        return args;
    }

    /// <summary>
    /// The fades for one moment: through black between moments, and out at the end (with in at the start, so the
    /// loop comes back from black). Each moment's own clock starts at 0, so a fade out starts a fade's length before
    /// the moment ends. The title is drawn after the moments are joined, so it stays put while the picture fades.
    /// </summary>
    public static string Fades(PosterSpec spec, int index, int count)
    {
        bool fadeIn = (spec.FadeBetween && index > 0) || (spec.FadeEnd && index == 0);
        bool fadeOut = (spec.FadeBetween && index < count - 1) || (spec.FadeEnd && index == count - 1);
        string d = Num(spec.FadeSeconds);
        return (fadeIn ? $",fade=t=in:st=0:d={d}" : string.Empty)
            + (fadeOut ? $",fade=t=out:st={Num(Math.Max(0, spec.MomentSeconds - spec.FadeSeconds))}:d={d}" : string.Empty);
    }

    /// <summary>
    /// The poster's drawing, as an ASS subtitle that lasts the whole clip: a soft dark band behind the title,
    /// a rounded white frame, and the title in capitals, wrapped onto as many lines as it needs.
    /// </summary>
    public static string BuildAss(PosterSpec spec, string title)
    {
        int w = spec.Width, h = spec.Height;
        var ass = new StringBuilder();
        ass.Append("[Script Info]\nScriptType: v4.00+\n");
        ass.Append(CultureInfo.InvariantCulture, $"PlayResX: {w}\nPlayResY: {h}\n");
        ass.Append("ScaledBorderAndShadow: yes\nWrapStyle: 0\n\n");

        string text = AssText(title);
        // Sized by the shorter side, so a wide library cover's title fits its height like a poster's fits its width.
        int side = Math.Min(w, h);
        int size = Math.Max(6, (int)Math.Round(TitleSize(side, text.Length) * spec.TextScale));
        int margin = (int)Math.Round(side * 0.09);
        int marginV = (int)Math.Round(h * 0.1);
        // ASS numpad alignment: 8 is top centre, 5 the middle, 2 the bottom.
        int alignment = spec.TextPosition switch { "top" => 8, "middle" => 5, _ => 2 };
        string shadow = spec.TextShadow ? Num(Math.Max(1, side / 160.0)) : "0";
        ass.Append("[V4+ Styles]\n");
        ass.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        ass.Append(CultureInfo.InvariantCulture, $"Style: Title,{spec.Font.Family},{size},&H00FFFFFF,&H00FFFFFF,&H00000000,&H80000000,0,0,0,0,100,100,1,0,1,0,{shadow},{alignment},{margin},{margin},{marginV},1\n");
        ass.Append("Style: Draw,Arial,10,&H00FFFFFF,&H00FFFFFF,&H00FFFFFF,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1\n\n");

        ass.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        const string Always = "0:00:00.00,9:59:59.00";
        if (spec.ShowTitle && spec.DarkBand && text.Length > 0)
        {
            // Darkens the part behind the title so white text reads on any picture; the blur softens its edges.
            // It runs past the picture's sides, so only the edges facing the picture show.
            int blur = Math.Max(4, side / 14);
            var (top, bottom) = spec.TextPosition switch
            {
                "top" => (-2 * blur, (int)Math.Round(h * 0.38)),
                "middle" => ((int)Math.Round(h * 0.3), (int)Math.Round(h * 0.7)),
                _ => ((int)Math.Round(h * 0.62), h + (2 * blur))
            };
            ass.Append(CultureInfo.InvariantCulture, $"Dialogue: 0,{Always},Draw,,0,0,0,,{{\\pos(0,0)\\an7\\bord0\\shad0\\1c&H000000&\\1a&H50&\\blur{blur}\\p1}}m {-2 * blur} {top} l {w + (2 * blur)} {top} {w + (2 * blur)} {bottom} {-2 * blur} {bottom}{{\\p0}}\n");
        }

        if (spec.ShowFrame)
        {
            double pad = Math.Round(side * 0.035, 1), radius = Math.Round(side * 0.045, 1), line = Math.Max(2, Math.Round(side / 80.0, 1));
            string outer = RoundedRect(pad, pad, w - pad, h - pad, radius, clockwise: true);
            string inner = RoundedRect(pad + line, pad + line, w - pad - line, h - pad - line, Math.Max(1, radius - line), clockwise: false);
            ass.Append(CultureInfo.InvariantCulture, $"Dialogue: 1,{Always},Draw,,0,0,0,,{{\\pos(0,0)\\an7\\bord0\\shad1\\4a&HA0&\\1c&HFFFFFF&\\p1}}{outer} {inner}{{\\p0}}\n");
        }

        if (spec.ShowTitle && text.Length > 0)
        {
            ass.Append(CultureInfo.InvariantCulture, $"Dialogue: 2,{Always},Title,,0,0,0,,{text}\n");
        }

        return ass.ToString();
    }

    /// <summary>Title size: big for a short title, smaller for a long one, so it fits in about three lines.</summary>
    public static int TitleSize(int width, int length) => (int)Math.Round(width * (length switch
    {
        <= 8 => 0.17,
        <= 16 => 0.15,
        <= 30 => 0.125,
        <= 50 => 0.1,
        _ => 0.085
    }));

    /// <summary>The title in capitals, with the characters ASS treats as commands made harmless.</summary>
    public static string AssText(string title)
    {
        var clean = string.Join(' ', new string((title ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.ToUpper(CultureInfo.InvariantCulture)
            .Replace('\\', '/')
            .Replace('{', '(')
            .Replace('}', ')');
    }

    /// <summary>A rounded rectangle as an ASS drawing; the inner one runs the other way so it cuts a hole.</summary>
    private static string RoundedRect(double x1, double y1, double x2, double y2, double r, bool clockwise)
    {
        // Bezier corners: k pulls the control points in, giving a near-circular arc.
        double k = r * 0.45;
        string P(double v) => Num(Math.Round(v, 1));
        if (clockwise)
        {
            return $"m {P(x1 + r)} {P(y1)} l {P(x2 - r)} {P(y1)} b {P(x2 - k)} {P(y1)} {P(x2)} {P(y1 + k)} {P(x2)} {P(y1 + r)} "
                + $"l {P(x2)} {P(y2 - r)} b {P(x2)} {P(y2 - k)} {P(x2 - k)} {P(y2)} {P(x2 - r)} {P(y2)} "
                + $"l {P(x1 + r)} {P(y2)} b {P(x1 + k)} {P(y2)} {P(x1)} {P(y2 - k)} {P(x1)} {P(y2 - r)} "
                + $"l {P(x1)} {P(y1 + r)} b {P(x1)} {P(y1 + k)} {P(x1 + k)} {P(y1)} {P(x1 + r)} {P(y1)}";
        }

        return $"m {P(x1 + r)} {P(y1)} b {P(x1 + k)} {P(y1)} {P(x1)} {P(y1 + k)} {P(x1)} {P(y1 + r)} "
            + $"l {P(x1)} {P(y2 - r)} b {P(x1)} {P(y2 - k)} {P(x1 + k)} {P(y2)} {P(x1 + r)} {P(y2)} "
            + $"l {P(x2 - r)} {P(y2)} b {P(x2 - k)} {P(y2)} {P(x2)} {P(y2 - k)} {P(x2)} {P(y2 - r)} "
            + $"l {P(x2)} {P(y1 + r)} b {P(x2)} {P(y1 + k)} {P(x2 - k)} {P(y1)} {P(x2 - r)} {P(y1)} l {P(x1 + r)} {P(y1)}";
    }

    private static async Task WriteFontAsync(string fontFile, string path, CancellationToken ct)
    {
        await using var font = typeof(PosterMaker).Assembly.GetManifestResourceStream("Jellyfin.Plugin.MedicPosters.Fonts." + fontFile)
            ?? throw new InvalidOperationException("The title font is missing from the plugin.");
        await using var file = File.Create(path);
        await font.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string LastLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no details";
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>Runs FFmpeg and returns its exit code and error output; -1 if it took too long.</summary>
    private static async Task<(int Exit, string Error)> RunAsync(string ffmpeg, string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var p = Process.Start(psi);
        if (p is null)
        {
            return (-2, "FFmpeg didn't start");
        }

        var error = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None); // drained so FFmpeg can't stall on a full pipe
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            return (-1, "timed out");
        }

        return (p.ExitCode, await error.ConfigureAwait(false));
    }
}

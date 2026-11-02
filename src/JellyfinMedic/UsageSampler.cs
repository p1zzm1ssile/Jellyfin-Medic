using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Every 5 minutes, counts how many people are watching and how many of those streams are
/// being transcoded, and adds it to an hour-of-the-week profile (and a quarter-hour one for the
/// planner). After a few days this shows your busy and quiet times.
/// </summary>
public sealed class UsageSampler : IHostedService, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly ISessionManager _sessions;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<UsageSampler> _logger;
    private Timer? _timer;

    public UsageSampler(ISessionManager sessions, IApplicationPaths paths, ILogger<UsageSampler> logger)
    {
        _sessions = sessions;
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Sample(), null, Every, Every);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    internal void Sample()
    {
        // Each on its own, so one that fails doesn't stop the others.
        List<object>? playing = null;
        (int Streams, int Transcodes) counts = default;
        Run("who is watching", () =>
        {
            playing = _sessions.Sessions
                .Cast<object>()
                .Where(s => SettingsReader.Get(s, "NowPlayingItem") is not null)
                .ToList();
            counts = Count(playing);
        });

        // Without the sessions these would record an empty hour, and the planner would take it as a quiet one.
        if (playing is { } watching)
        {
            Run("busy hours", () => UsageStore.Record(_paths, DateTime.Now, counts.Streams, counts.Transcodes));
            Run("transcodes", () => TranscodeLog.Record(_paths, watching));
        }

        Run("the live readings", ServerNow.Record);
        Run("disk space", () => StorageWatch.RecordKnown(_paths.PluginConfigurationsPath));
    }

    // What failed last time, so a failure that repeats every 5 minutes is a warning once, not every time.
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    private void Run(string what, Action record)
    {
        try
        {
            record();
            if (_failing.Remove(what))
            {
                _logger.LogInformation("Jellyfin Medic: recording {What} works again.", what);
            }
        }
        catch (Exception ex)
        {
            Failed(what, ex);
        }
    }

    private void Failed(string what, Exception ex)
    {
        if (_failing.Add(what))
        {
            _logger.LogWarning(ex, "Jellyfin Medic: couldn't record {What}; Medic keeps trying every 5 minutes.", what);
        }
        else
        {
            _logger.LogDebug(ex, "Jellyfin Medic: still couldn't record {What}.", what);
        }
    }

    /// <summary>
    /// How many streams, and how many transcodes, these sessions with something playing make up. A paused session is
    /// neither, and only a transcode that converts video counts: converting just the audio, or music, is light work
    /// that hardware acceleration doesn't help with, and the share of transcodes is what tells you to turn it on.
    /// </summary>
    internal static (int Streams, int Transcodes) Count(IEnumerable<object> playing)
    {
        var streams = playing.Where(s => SettingsReader.Bool(s, "PlayState.IsPaused") != true).ToList();
        int transcodes = streams.Count(s =>
            string.Equals(SettingsReader.Text(s, "PlayState.PlayMethod"), "Transcode", StringComparison.OrdinalIgnoreCase)
            // Music has no video, yet reports it as not copied.
            && string.Equals(SettingsReader.Text(s, "NowPlayingItem.MediaType"), "Video", StringComparison.OrdinalIgnoreCase)
            // TranscodingInfo is null until FFmpeg first reports progress, so a transcode counts until it says the video is copied.
            && SettingsReader.Bool(s, "TranscodingInfo.IsVideoDirect") != true);
        return (streams.Count, transcodes);
    }
}

public static class UsageStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string FilePath(IApplicationPaths paths) =>
        Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "usage_profile.json");

    public static UsageProfile Load(IApplicationPaths paths)
    {
        lock (Sync)
        {
            return LoadUnlocked(paths, forReading: true);
        }
    }

    public static void Record(IApplicationPaths paths, DateTime localNow, int streams, int transcodes)
    {
        lock (Sync)
        {
            var profile = LoadUnlocked(paths, forReading: false);
            int index = (((int)localNow.DayOfWeek + 6) % 7) * 24 + localNow.Hour;
            var bucket = profile.Buckets[index];

            bucket.Samples++;
            bucket.StreamSum += streams;
            bucket.TranscodeSum += transcodes;
            bucket.MaxStreams = Math.Max(bucket.MaxStreams, streams);
            bucket.MaxTranscodes = Math.Max(bucket.MaxTranscodes, transcodes);

            int quarter = (((int)localNow.DayOfWeek + 6) % 7) * 96 + localNow.Hour * 4 + localNow.Minute / 15;
            profile.QuarterSamples[quarter]++;
            profile.QuarterStreamSums[quarter] += streams;

            profile.TotalSamples++;
            profile.FirstSampleUtc ??= DateTime.UtcNow;
            profile.LastSampleUtc = DateTime.UtcNow;

            string path = FilePath(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ScheduleStorage.WriteText(path, JsonSerializer.Serialize(profile, Indented));
        }
    }

    private static UsageProfile LoadUnlocked(IApplicationPaths paths, bool forReading)
    {
        string path = FilePath(paths);
        UsageProfile? profile = null;
        try
        {
            if (File.Exists(path))
            {
                profile = JsonSerializer.Deserialize<UsageProfile>(File.ReadAllText(path));
            }
        }
        catch (JsonException ex)
        {
            try
            {
                ScheduleStorage.SetAside(path, ex);
            }
            catch (Exception moveFailed) when (forReading && moveFailed is IOException or UnauthorizedAccessException)
            {
                // Reading only shows the profile, so the report, the usage page and the planner get an empty one and
                // carry on. Recording still fails, so nothing writes over the damaged file before it's set aside.
                ScheduleStorage.Logger?.LogWarning(moveFailed, "Jellyfin Medic: {File} couldn't be read or kept aside as .bad, so it reads as empty", Path.GetFileName(path));
            }

            profile = null;
        }

        profile ??= new UsageProfile();
        while (profile.Buckets.Count < 168)
        {
            profile.Buckets.Add(new UsageBucket());
        }

        while (profile.QuarterSamples.Count < 672)
        {
            profile.QuarterSamples.Add(0);
        }

        while (profile.QuarterStreamSums.Count < 672)
        {
            profile.QuarterStreamSums.Add(0);
        }

        return profile;
    }
}

public static class UsageAnalyzer
{
    private static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    public static UsageSummary Summarise(UsageProfile profile)
    {
        var summary = new UsageSummary
        {
            // One sample every 5 minutes = 12 per hour.
            HoursCollected = profile.TotalSamples / 12.0
        };

        summary.Ready = summary.HoursCollected >= 72;
        summary.AverageStreams = profile.Buckets
            .Select(b => b.Samples == 0 ? 0 : Math.Round(b.StreamSum / (double)b.Samples, 2))
            .ToList();
        summary.SampleCounts = profile.Buckets.Select(b => b.Samples).ToList();
        summary.QuarterSampleCounts = profile.QuarterSamples.ToList();
        summary.QuarterAverageStreams = profile.QuarterSamples
            .Select((n, i) => n == 0 ? 0 : profile.QuarterStreamSums[i] / (double)n)
            .ToList();
        summary.MaxStreams = profile.Buckets.Max(b => b.MaxStreams);
        summary.MaxTranscodes = profile.Buckets.Max(b => b.MaxTranscodes);

        long streamSum = profile.Buckets.Sum(b => b.StreamSum);
        long transcodeSum = profile.Buckets.Sum(b => b.TranscodeSum);
        summary.TranscodeShare = streamSum > 0 ? transcodeSum / (double)streamSum : null;

        // Average for each hour of the day across the whole week.
        var byHour = Enumerable.Range(0, 24)
            .Select(h => Enumerable.Range(0, 7).Average(d => summary.AverageStreams[d * 24 + h]))
            .ToArray();

        double peak = byHour.Max();
        if (peak > 0)
        {
            var busy = Enumerable.Range(0, 24).Where(h => byHour[h] >= peak * 0.6).ToList();
            summary.BusiestHours = DescribeHours(busy);

            int busiestDay = Enumerable.Range(0, 7)
                .OrderByDescending(d => Enumerable.Range(0, 24).Sum(h => summary.AverageStreams[d * 24 + h]))
                .First();
            summary.BusiestDay = Days[busiestDay];
        }

        // Quietest 5 consecutive hours (wrapping past midnight). Ties go to the start nearest 01:00.
        const int window = 5;
        int bestStart = 1;
        double bestLoad = double.MaxValue;
        foreach (int start in Enumerable.Range(0, 24).OrderBy(s => Math.Min(Math.Abs(s - 1), 24 - Math.Abs(s - 1))))
        {
            double load = Enumerable.Range(0, window).Sum(i => byHour[(start + i) % 24]);
            if (load < bestLoad - 0.0001)
            {
                bestLoad = load;
                bestStart = start;
            }
        }

        summary.QuietWindow = $"{bestStart:00}:00–{(bestStart + window) % 24:00}:00";

        return summary;
    }

    /// <summary>Turns [19, 20, 21, 22] into "19:00–23:00".</summary>
    private static string DescribeHours(List<int> hours)
    {
        if (hours.Count == 0)
        {
            return "none";
        }

        var ranges = new List<string>();
        int start = hours[0];
        int previous = hours[0];
        foreach (int hour in hours.Skip(1).Append(-1))
        {
            if (hour == previous + 1)
            {
                previous = hour;
                continue;
            }

            ranges.Add($"{start:00}:00–{(previous + 1) % 24:00}:00");
            start = hour;
            previous = hour;
        }

        return string.Join(", ", ranges);
    }
}

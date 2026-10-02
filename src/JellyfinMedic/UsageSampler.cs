using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Every 5 minutes, counts how many people are watching and how many of those streams are
/// being transcoded, and adds it to an hour-of-the-week profile. After a few days this shows
/// your busy and quiet times.
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

    private void Sample()
    {
        try
        {
            var playing = _sessions.Sessions
                .Cast<object>()
                .Where(s => SettingsReader.Get(s, "NowPlayingItem") is not null)
                .ToList();

            int streams = playing.Count;
            int transcodes = playing.Count(s =>
                string.Equals(SettingsReader.Text(s, "PlayState.PlayMethod"), "Transcode", StringComparison.OrdinalIgnoreCase));

            UsageStore.Record(_paths, DateTime.Now, streams, transcodes);
            ServerNow.Record();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jellyfin Medic: couldn't record usage this time.");
        }
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
            return LoadUnlocked(paths);
        }
    }

    public static void Record(IApplicationPaths paths, DateTime localNow, int streams, int transcodes)
    {
        lock (Sync)
        {
            var profile = LoadUnlocked(paths);
            int index = (((int)localNow.DayOfWeek + 6) % 7) * 24 + localNow.Hour;
            var bucket = profile.Buckets[index];

            bucket.Samples++;
            bucket.StreamSum += streams;
            bucket.TranscodeSum += transcodes;
            bucket.MaxStreams = Math.Max(bucket.MaxStreams, streams);
            bucket.MaxTranscodes = Math.Max(bucket.MaxTranscodes, transcodes);

            profile.TotalSamples++;
            profile.FirstSampleUtc ??= DateTime.UtcNow;
            profile.LastSampleUtc = DateTime.UtcNow;

            string path = FilePath(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, Indented));
        }
    }

    private static UsageProfile LoadUnlocked(IApplicationPaths paths)
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
        catch (JsonException)
        {
            profile = null;
        }

        profile ??= new UsageProfile();
        while (profile.Buckets.Count < 168)
        {
            profile.Buckets.Add(new UsageBucket());
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
        summary.MaxStreams = profile.Buckets.Max(b => b.MaxStreams);
        summary.MaxTranscodes = profile.Buckets.Max(b => b.MaxTranscodes);

        long streamSum = profile.Buckets.Sum(b => b.StreamSum);
        long transcodeSum = profile.Buckets.Sum(b => b.TranscodeSum);
        summary.TranscodeShare = streamSum > 0 ? transcodeSum / (double)streamSum : null;

        // Average for each hour of the day across the whole week.
        var byHour = Enumerable.Range(0, 24)
            .Select(h => Enumerable.Range(0, 7).Average(d => summary.AverageStreams[d * 24 + h]))
            .ToArray();

        summary.NightAverage = Enumerable.Range(1, 5).Average(h => byHour[h]);

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

        summary.QuietStartHour = bestStart;
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

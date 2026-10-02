using System;
using System.Collections.Generic;
using System.Linq;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// How busy each hour of the week usually is (average number of people watching), used by the
/// planner to put heavy tasks in quiet spells. Comes from Medic's own viewing records once there
/// are a few days of them; until then a typical household pattern is used (quiet overnight,
/// busy in the evenings). Hours the admin has told Medic to avoid are never used.
/// </summary>
public sealed class BusyProfile
{
    // Typical household, by hour of day, in "people watching". Only the shape matters.
    private static readonly double[] Typical =
    {
        0.6, 0.2, 0.05, 0, 0, 0, 0.05, 0.2, 0.3, 0.3, 0.3, 0.3,
        0.35, 0.35, 0.35, 0.4, 0.5, 0.8, 1, 1, 1, 1, 0.9, 0.8
    };

    private readonly double[] _load = new double[168]; // index = Monday-first day * 24 + hour
    private readonly bool[] _avoid = new bool[24];

    public bool FromViewing { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    public bool HasAvoidedHours => _avoid.Any(a => a);

    public string PlacementNote => FromViewing
        ? "Timed for a quiet spell in your viewing pattern."
        : "Timed for the usual quiet hours until Medic has learned when people watch.";

    public static BusyProfile Create(UsageSummary usage, PluginConfiguration? settings)
    {
        var profile = new BusyProfile();

        if (settings is { AvoidEnabled: true })
        {
            int from = Math.Clamp(settings.AvoidStartHour, 0, 23);
            int to = ((settings.AvoidEndHour % 24) + 24) % 24;
            if (from != to)
            {
                // Wraps past midnight if needed, e.g. 22:00 to 02:00.
                int h = from;
                do
                {
                    profile._avoid[h] = true;
                    h = (h + 1) % 24;
                }
                while (h != to);
            }
        }

        bool haveData = usage.Ready && usage.AverageStreams.Count == 168;
        if (haveData)
        {
            var samples = usage.SampleCounts.Count == 168 ? usage.SampleCounts : Enumerable.Repeat(1, 168).ToList();

            // An hour never sampled on a given weekday takes that hour's average from the days that were.
            var hourAverage = new double[24];
            for (int h = 0; h < 24; h++)
            {
                var seen = Enumerable.Range(0, 7).Where(d => samples[d * 24 + h] > 0).Select(d => usage.AverageStreams[d * 24 + h]).ToList();
                hourAverage[h] = seen.Count > 0 ? seen.Average() : Typical[h];
            }

            for (int i = 0; i < 168; i++)
            {
                profile._load[i] = samples[i] > 0 ? usage.AverageStreams[i] : hourAverage[i % 24];
            }

            profile.FromViewing = true;
            profile.Summary = $"Using your viewing pattern from the last {Math.Max(1, (int)Math.Round(usage.HoursCollected / 24))} days.";
        }
        else
        {
            for (int i = 0; i < 168; i++)
            {
                profile._load[i] = Typical[i % 24];
            }

            profile.Summary = $"Still learning when people watch ({(int)usage.HoursCollected} hours recorded). Until then Medic assumes a typical household: quiet overnight, busy in the evenings.";
        }

        if (profile.HasAvoidedHours)
        {
            profile.Summary += $" Never schedules tasks between {settings!.AvoidStartHour:00}:00 and {settings.AvoidEndHour % 24:00}:00.";
        }

        return profile;
    }

    public double At(DayOfWeek day, int hour) => _load[ScheduleStorage.MondayFirst(day) * 24 + (((hour % 24) + 24) % 24)];

    /// <summary>Expected people watching x hours over a run starting at a cell.</summary>
    public double CostOver(DayOfWeek day, int startCell, int cells, int cellMinutes)
    {
        double sum = 0;
        for (int c = startCell; c < startCell + cells; c++)
        {
            sum += At(day, c * cellMinutes / 60) * cellMinutes / 60.0;
        }

        return sum;
    }

    /// <summary>The busiest hour a run would overlap, and how busy it is.</summary>
    public (double Peak, int Hour) PeakOver(DayOfWeek day, int startMinute, int minutes)
    {
        double peak = 0;
        int peakHour = startMinute / 60;
        for (int m = startMinute; m < startMinute + Math.Max(1, minutes); m += 15)
        {
            int hour = m / 60 % 24;
            double value = At(day, hour);
            if (value > peak)
            {
                peak = value;
                peakHour = hour;
            }
        }

        return (peak, peakHour);
    }

    /// <summary>Marks the avoided hours as taken on every day, so nothing is placed there.</summary>
    public void BlockAvoidedHours(bool[,] grid, int cellMinutes)
    {
        int cellsPerDay = grid.GetLength(1);
        for (int d = 0; d < 7; d++)
        {
            for (int cell = 0; cell < cellsPerDay; cell++)
            {
                if (_avoid[cell * cellMinutes / 60 % 24])
                {
                    grid[d, cell] = true;
                }
            }
        }
    }

    /// <summary>The 24 hourly values for one day, scaled 0 to 1 for shading the timeline.</summary>
    public List<double> Shading(DayOfWeek day)
    {
        double max = Math.Max(0.5, _load.Max());
        return Enumerable.Range(0, 24).Select(h => Math.Round(Math.Min(1, At(day, h) / max), 2)).ToList();
    }

    public List<bool> AvoidedHours() => _avoid.ToList();
}

using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// How busy each quarter hour of the week usually is (average number of people watching), used by
/// the planner to put heavy tasks in quiet spells. Comes from Medic's own viewing records once there
/// are a few days of them; until then a typical household pattern is used (quiet overnight, busy in
/// the evenings). Times the admin has told Medic to avoid are never used.
///
/// Viewing is recorded per hour and per quarter hour. Each quarter hour starts from a smooth curve
/// through the hourly averages, and leans on its own samples as they build up. Without that the
/// cost is flat within each hour, so the quietest start always lands on the hour.
/// </summary>
public sealed class BusyProfile
{
    public const int SlotMinutes = 15;
    public const int SlotsPerDay = 24 * 60 / SlotMinutes;
    private const int SlotsPerWeek = 7 * SlotsPerDay;

    // Samples of one quarter hour before its own average is trusted fully (about three weeks).
    private const double QuarterTrustSamples = 9;

    // Typical household, by hour of day, in "people watching". Only the shape matters.
    private static readonly double[] Typical =
    {
        0.6, 0.2, 0.05, 0, 0, 0, 0.05, 0.2, 0.3, 0.3, 0.3, 0.3,
        0.35, 0.35, 0.35, 0.4, 0.5, 0.8, 1, 1, 1, 1, 0.9, 0.8
    };

    private readonly double[] _load = new double[SlotsPerWeek]; // index = Monday-first day * 96 + quarter hour
    private readonly bool[] _avoid = new bool[SlotsPerDay];

    public bool FromViewing { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    public bool HasAvoidedTimes => _avoid.Any(a => a);

    public string PlacementNote => FromViewing
        ? "Timed for a quiet spell in your viewing pattern."
        : "Timed for the usual quiet hours until Medic has learned when people watch.";

    /// <summary>Start of the avoided window, in minutes after midnight.</summary>
    public static int AvoidStartMinute(PluginConfiguration settings) =>
        Math.Clamp(settings.AvoidStartMinute ?? settings.AvoidStartHour * 60, 0, 1439) / SlotMinutes * SlotMinutes;

    /// <summary>End of the avoided window, in minutes after midnight (24:00 is 00:00).</summary>
    public static int AvoidEndMinute(PluginConfiguration settings) =>
        (Math.Clamp(settings.AvoidEndMinute ?? settings.AvoidEndHour * 60, 0, 1440) % 1440) / SlotMinutes * SlotMinutes;

    public static BusyProfile Create(UsageSummary usage, PluginConfiguration? settings)
    {
        var profile = new BusyProfile();

        if (settings is { AvoidEnabled: true })
        {
            int from = AvoidStartMinute(settings) / SlotMinutes;
            int to = AvoidEndMinute(settings) / SlotMinutes;
            if (from != to)
            {
                // Wraps past midnight if needed, e.g. 22:00 to 02:00.
                int slot = from;
                do
                {
                    profile._avoid[slot] = true;
                    slot = (slot + 1) % SlotsPerDay;
                }
                while (slot != to);
            }
        }

        var hourly = new double[168];
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
                hourly[i] = samples[i] > 0 ? usage.AverageStreams[i] : hourAverage[i % 24];
            }

            profile.FromViewing = true;
            profile.Summary = $"Using your viewing pattern from the last {Math.Max(1, (int)Math.Round(usage.HoursCollected / 24))} days.";
        }
        else
        {
            for (int i = 0; i < 168; i++)
            {
                hourly[i] = Typical[i % 24];
            }

            profile.Summary = $"Still learning when people watch ({(int)usage.HoursCollected} hours recorded). Until then Medic assumes a typical household: quiet overnight, busy in the evenings.";
        }

        bool haveQuarters = haveData && usage.QuarterAverageStreams.Count == SlotsPerWeek && usage.QuarterSampleCounts.Count == SlotsPerWeek;
        for (int i = 0; i < SlotsPerWeek; i++)
        {
            // Straight line between the middles of the hours either side, wrapping round the week.
            double hourPos = (i + 0.5) * SlotMinutes / 60.0 - 0.5;
            int before = (int)Math.Floor(hourPos);
            double t = hourPos - before;
            double smooth = (hourly[(before + 168) % 168] * (1 - t)) + (hourly[(before + 1) % 168] * t);

            double trust = haveQuarters ? Math.Min(1, usage.QuarterSampleCounts[i] / QuarterTrustSamples) : 0;
            profile._load[i] = trust > 0
                ? (usage.QuarterAverageStreams[i] * trust) + (smooth * (1 - trust))
                : smooth;
        }

        if (profile.HasAvoidedTimes)
        {
            profile.Summary += $" Never schedules tasks between {Clock(AvoidStartMinute(settings!))} and {Clock(AvoidEndMinute(settings!))}.";
        }

        return profile;
    }

    private static string Clock(int minute) => ScheduleStorage.Hhmm(TimeSpan.FromMinutes(minute));

    /// <summary>People usually watching at a time of day; minutes past midnight run on into the next day.</summary>
    public double At(DayOfWeek day, int minute)
    {
        int index = (ScheduleStorage.MondayFirst(day) * SlotsPerDay) + (int)Math.Floor(minute / (double)SlotMinutes);
        return _load[((index % SlotsPerWeek) + SlotsPerWeek) % SlotsPerWeek];
    }

    /// <summary>Expected people watching x hours over a run starting at a cell.</summary>
    public double CostOver(DayOfWeek day, int startCell, int cells, int cellMinutes)
    {
        double sum = 0;
        for (int c = startCell; c < startCell + cells; c++)
        {
            sum += At(day, c * cellMinutes) * cellMinutes / 60.0;
        }

        return sum;
    }

    /// <summary>The busiest quarter hour a run would overlap (minutes past midnight), and how busy it is.</summary>
    public (double Peak, int Minute) PeakOver(DayOfWeek day, int startMinute, int minutes)
    {
        double peak = 0;
        int peakMinute = startMinute / SlotMinutes * SlotMinutes;
        for (int m = startMinute; m < startMinute + Math.Max(1, minutes); m += SlotMinutes)
        {
            double value = At(day, m);
            if (value > peak)
            {
                peak = value;
                peakMinute = m / SlotMinutes * SlotMinutes % 1440;
            }
        }

        return (peak, peakMinute);
    }

    /// <summary>Marks the avoided times as taken on every day, so nothing is placed there.</summary>
    public void BlockAvoidedTimes(bool[,] grid, int cellMinutes)
    {
        int cellsPerDay = grid.GetLength(1);
        for (int d = 0; d < 7; d++)
        {
            for (int cell = 0; cell < cellsPerDay; cell++)
            {
                if (_avoid[cell * cellMinutes / SlotMinutes % SlotsPerDay])
                {
                    grid[d, cell] = true;
                }
            }
        }
    }

    /// <summary>The 96 quarter-hour values for one day, scaled 0 to 1 for shading the timeline.</summary>
    public List<double> Shading(DayOfWeek day)
    {
        double max = Math.Max(0.5, _load.Max());
        return Enumerable.Range(0, SlotsPerDay).Select(s => Math.Round(Math.Min(1, At(day, s * SlotMinutes) / max), 2)).ToList();
    }

    public List<bool> AvoidedSlots() => _avoid.ToList();
}

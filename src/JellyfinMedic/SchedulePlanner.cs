using MediaBrowser.Model.Tasks;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

public enum Cadence
{
    LeaveAlone,
    Daily,
    EveryOtherDay,
    TwiceWeekly,
    Weekly,
    Monthly,
    Off
}

/// <summary>
/// One task in the recommended schedule. Preview shows these and Apply writes them,
/// so what you preview is exactly what gets applied.
/// </summary>
public sealed class PlannedTask
{
    public IScheduledTaskWorker Worker { get; set; } = null!;

    public Cadence Cadence { get; set; }

    public string CadenceLabel { get; set; } = string.Empty;

    public string CurrentSchedule { get; set; } = string.Empty;

    public string ProposedSchedule { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string? Warning { get; set; }

    // Jellyfin triggers to write. Null when the task is left alone or run monthly by Medic.
    public TaskTriggerInfo[]? Triggers { get; set; }

    // Set when Medic will run the task itself (monthly).
    public ManagedRun? Managed { get; set; }

    public int StartMinutes { get; set; } = int.MaxValue;

    /// <summary>The owner's choice for this task: medic, keep or off.</summary>
    public string Choice { get; set; } = ScheduleStorage.ChoiceMedic;

    /// <summary>False for tasks Medic doesn't know how to plan, which can only be kept or turned off.</summary>
    public bool CanPlan { get; set; } = true;

    public bool Changes => Cadence != Cadence.LeaveAlone && CurrentSchedule != ProposedSchedule;
}

/// <summary>
/// Decides how often each known task needs to run and when.
///
/// How often: every known task has a "need" (for example, the IPTV guide must refresh
/// daily, while chapter images only process new items and can be batched). Where the
/// need allows it, the recorded run time decides the final cadence: a task that finishes
/// in seconds can stay daily, a task that takes an hour gets spread out.
///
/// When: tasks are fitted into 15-minute blocks, sized from their run time plus a buffer, so
/// no two planned tasks overlap. Each night runs in stages: the library scan first, then the
/// tasks that build on it, then upkeep. A task is only put before an earlier stage when every
/// slot after it is much busier. Tasks that don't run every night are spread across nights.
/// </summary>
public static class SchedulePlanner
{
    private const int CellMinutes = 15;
    private const int CellsPerDay = 24 * 60 / CellMinutes;
    private const double BufferMinutes = 5;
    private const double MaxBlockMinutes = 720;
    private const double BufferShare = 0.25;          // runs vary, so leave a quarter of the run time spare (at least 5 minutes)
    private const double CheapMinutes = 5;
    private const double HeavyMinutes = 45;
    private const double LeftAloneDefaultMinutes = 15;

    // Cost tuning (units: people watching x hours).
    private const double BusyThreshold = 0.5;      // warn when about half a person or more is usually watching
    private const double SameTimeTolerance = 0.5;   // keep one daily time unless per-day times are clearly quieter
    private const double BeforeEarlierStage = 0.75; // starting before an earlier stage has finished that night
    private const double FollowNudge = 0.002;       // per 15 minutes of gap after the earlier stages, to keep the night together
    private const double SpreadNudge = 0.002;       // prefer days with less already planned
    private const double TieBreakNudge = 0.0001;    // prefer the early hours when all else is equal
    private const int EarlyHoursCell = 4;           // 01:00
    private const int NoonCell = 12 * 60 / CellMinutes; // the night is ordered from noon to noon

    private static readonly DayOfWeek[] AllDays =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    // Jellyfin can't repeat "every 2 days" or "every 3 days" at a fixed time, so these
    // cadences use fixed weekdays instead. That keeps each task on the same nights every week.
    private static readonly DayOfWeek[][] EveryOtherDaySets =
    {
        new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
        new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday },
        new[] { DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Sunday }
    };

    private static readonly DayOfWeek[][] TwiceWeeklySets =
    {
        new[] { DayOfWeek.Monday, DayOfWeek.Thursday },
        new[] { DayOfWeek.Tuesday, DayOfWeek.Friday },
        new[] { DayOfWeek.Wednesday, DayOfWeek.Saturday },
        new[] { DayOfWeek.Thursday, DayOfWeek.Sunday }
    };

    private static readonly DayOfWeek[][] WeeklySets = AllDays.Select(d => new[] { d }).ToArray();

    // First match wins, so more specific names come first.
    // Cheap / Moderate / Heavy: the cadence to use when the task's average run is
    // under 5 minutes / 5 to 45 minutes / 45 minutes or more.
    private static readonly TaskRule[] Rules =
    {
        new("Scan Media Library", Cadence.Daily, Cadence.Daily, Cadence.Daily, 0, 60,
            "New IPTV and local titles should appear by morning, and later tasks need an up-to-date library, so it runs daily and first."),
        new("Guide", Cadence.Daily, Cadence.Daily, Cadence.Daily, 1, 20,
            "The IPTV guide changes every day. It gets its own slot after the library scan, so their database writes never overlap."),
        new("Transcode", Cadence.Daily, Cadence.Daily, Cadence.Daily, 1, 5,
            "Clears leftover transcode files so the cache drive doesn't fill up."),
        new("Subtitle", Cadence.TwiceWeekly, Cadence.TwiceWeekly, Cadence.TwiceWeekly, 2, 20,
            "Searching less often keeps you under subtitle provider limits."),
        new("Chapter Image", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("Trickplay", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("Media Segment", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("Keyframe", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("Intro", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added episodes, so batching them up loses nothing."),
        new("Audio Normali", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 15,
            "Only works on newly added music, so batching it up loses nothing."),
        new("Optimize", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 15,
            "Keeps the database tidy. Weekly is enough and avoids extra writes to the cache drive."),
        new("Cache Directory", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 5,
            "Clears old cached images and files. Weekly is plenty."),
        new("Log Directory", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 5,
            "Removes old log files. Weekly is plenty."),
        new("Update Plugins", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 5,
            "Weekly keeps plugins current without surprise changes mid-week."),
        new("People", Cadence.Weekly, Cadence.Weekly, Cadence.Monthly, 4, 60,
            "Refreshes cast and crew details, which rarely change."),
        new("Activity Log", Cadence.Monthly, Cadence.Monthly, Cadence.Monthly, 4, 5,
            "Only trims old entries from the activity log.")
    };

    public static List<PlannedTask> Build(
        IReadOnlyList<IScheduledTaskWorker> workers,
        IReadOnlyDictionary<string, ReliabilityRecord> profile,
        IReadOnlyDictionary<string, ManagedRun> managed,
        BusyProfile busy,
        IReadOnlyDictionary<string, string>? choices = null)
    {
        var grid = new bool[7, CellsPerDay];
        busy.BlockAvoidedTimes(grid, CellMinutes);

        var planned = new List<PlannedTask>();
        var leftAlone = new List<PlannedTask>();
        var candidates = new List<Candidate>();
        var night = new NightOrder();

        foreach (var worker in workers)
        {
            string id = worker.Id.ToString();
            profile.TryGetValue(id, out var rec);
            managed.TryGetValue(id, out var managedRun);

            var rule = Rules.FirstOrDefault(r => worker.Name.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase));
            var (minutes, evidence) = MeasureCost(worker, rec);
            string current = ScheduleStorage.FormatSchedule(worker.Triggers, managedRun);

            // A task with no schedule is never given one unless the owner asks for it.
            bool unscheduled = managedRun is null && (worker.Triggers is null || !worker.Triggers.Any());
            string choice = choices is not null && choices.TryGetValue(id, out var chosen)
                ? chosen
                : unscheduled ? ScheduleStorage.ChoiceKeep : ScheduleStorage.ChoiceMedic;
            if (rule is null && choice == ScheduleStorage.ChoiceMedic)
            {
                choice = ScheduleStorage.ChoiceKeep;
            }

            if (choice == ScheduleStorage.ChoiceOff)
            {
                leftAlone.Add(TurnOff(worker, current, rule is not null));
                continue;
            }

            if (choice == ScheduleStorage.ChoiceKeep)
            {
                // Keep its times, but block them out so planned tasks avoid them.
                ReserveExisting(grid, worker.Triggers, minutes ?? LeftAloneDefaultMinutes);
                string why = rule is null
                    ? "Medic doesn't know what this task needs, so it's left as it is."
                    : unscheduled && (choices is null || !choices.ContainsKey(id))
                        ? "It has no schedule now, so Medic leaves it that way. Choose \"Let Medic schedule it\" to give it one."
                        : "You chose to keep its current schedule.";
                var kept = LeaveAlone(worker, current, why);
                kept.CanPlan = rule is not null;
                leftAlone.Add(kept);
                continue;
            }

            // Unknown tasks were kept or turned off above, so there is a rule here.
            candidates.Add(new Candidate(worker, rule!, rec, managedRun, current, minutes, evidence));
        }

        // Library scan first, then the tasks that build on it; within a stage, the tasks that
        // run most often first, since they need room on the most days.
        foreach (var (candidate, cadence) in candidates
                     .Select(c => (Candidate: c, Cadence: ChooseCadence(c.Rule, c.Minutes)))
                     .OrderBy(x => x.Candidate.Rule.Stage)
                     .ThenBy(x => (int)x.Cadence)
                     .ThenByDescending(x => x.Candidate.Minutes ?? x.Candidate.Rule.DefaultMinutes)
                     .ThenBy(x => x.Candidate.Worker.Name, StringComparer.OrdinalIgnoreCase))
        {
            night.BeginStage(candidate.Rule.Stage);
            planned.Add(Place(grid, busy, night, candidate, cadence));
        }

        // In the order the night runs (noon to noon), so 23:00 comes before 01:00.
        return planned
            .OrderBy(p => (p.StartMinutes - (NoonCell * CellMinutes) + (24 * 60)) % (24 * 60))
            .ThenBy(p => p.Worker.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(leftAlone.OrderBy(p => p.Worker.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static PlannedTask Place(bool[,] grid, BusyProfile busy, NightOrder night, Candidate c, Cadence cadence)
    {
        double estimate = c.Minutes ?? c.Rule.DefaultMinutes;
        int length = BlockCells(estimate);
        long? maxRuntime = MaxRuntime(c.Worker);

        // Each day the task runs, with the cell it starts at.
        var slots = new List<(DayOfWeek Day, int Start)>();

        if (cadence == Cadence.Daily)
        {
            // One time every day is simplest. Only use different times on different days when
            // that is clearly quieter (your quiet hours differ through the week).
            var same = BestCommonStart(grid, busy, night, AllDays, length);
            var perDay = AllDays.Select(d => (Day: d, Best: BestCommonStart(grid, busy, night, new[] { d }, length))).ToList();
            bool perDayPossible = perDay.All(p => p.Best is not null);
            double perDayCost = perDayPossible ? perDay.Sum(p => p.Best!.Value.Cost) : double.MaxValue;

            if (same is { } s && s.Cost <= perDayCost + SameTimeTolerance)
            {
                slots.AddRange(AllDays.Select(d => (d, s.Start)));
            }
            else if (perDayPossible)
            {
                slots.AddRange(perDay.Select(p => (p.Day, p.Best!.Value.Start)));
            }
        }
        else
        {
            var options = cadence switch
            {
                Cadence.EveryOtherDay => EveryOtherDaySets,
                Cadence.TwiceWeekly => TwiceWeeklySets,
                _ => WeeklySets // Weekly, and Monthly (which runs on the first of a chosen weekday)
            };

            (DayOfWeek[] Days, int Start, double Cost)? best = null;
            foreach (var days in options)
            {
                if (BestCommonStart(grid, busy, night, days, length) is { } found &&
                    (best is null || found.Cost < best.Value.Cost))
                {
                    best = (days, found.Start, found.Cost);
                }
            }

            if (best is { } b)
            {
                slots.AddRange(b.Days.Select(d => (d, b.Start)));
            }
        }

        if (slots.Count == 0)
        {
            return LeaveAlone(c.Worker, c.Current, "There was no free slot left for this task, so it's left as it is.");
        }

        foreach (var (day, start) in slots)
        {
            Mark(grid, new[] { day }, start, length);
            night.Placed(day, start, length);
        }

        var warnings = CommonWarnings(c);
        if (busy.FromViewing)
        {
            var (peak, peakMinute) = slots
                .Select(sl => busy.PeakOver(sl.Day, sl.Start * CellMinutes, (int)Math.Ceiling(estimate)))
                .OrderByDescending(x => x.Peak)
                .First();
            if (peak >= BusyThreshold)
            {
                warnings.Insert(0, $"Runs when people often watch (about {peak:0.#} watching around {ScheduleStorage.Hhmm(TimeSpan.FromMinutes(peakMinute))}). There was no quieter free time for it.");
            }
        }

        int firstStart = slots.Min(sl => sl.Start);
        var result = new PlannedTask
        {
            Worker = c.Worker,
            Cadence = cadence,
            CadenceLabel = CadenceLabel(cadence),
            CurrentSchedule = c.Current,
            Reason = BuildReason(c) + " " + busy.PlacementNote,
            Warning = JoinWarnings(warnings),
            StartMinutes = firstStart * CellMinutes
        };

        if (cadence == Cadence.Monthly)
        {
            // Jellyfin has no monthly trigger, so Medic runs the task itself
            // on the first of the chosen weekday each month.
            result.Managed = new ManagedRun
            {
                TaskId = c.Worker.Id.ToString(),
                TaskName = c.Worker.Name,
                Weekday = slots[0].Day,
                TimeOfDayTicks = TimeSpan.FromMinutes(slots[0].Start * CellMinutes).Ticks,
                LastStartedLocal = c.Managed?.LastStartedLocal
            };
            result.ProposedSchedule = ScheduleStorage.FormatManaged(result.Managed);
        }
        else
        {
            bool oneTimeEveryDay = cadence == Cadence.Daily && slots.Select(sl => sl.Start).Distinct().Count() == 1;
            result.Triggers = oneTimeEveryDay
                ? new[] { Daily(TimeSpan.FromMinutes(slots[0].Start * CellMinutes), maxRuntime) }
                : slots
                    .OrderBy(sl => ScheduleStorage.MondayFirst(sl.Day))
                    .Select(sl => Weekly(sl.Day, TimeSpan.FromMinutes(sl.Start * CellMinutes), maxRuntime))
                    .ToArray();
            result.ProposedSchedule = ScheduleStorage.FormatTriggers(result.Triggers);
        }

        return result;
    }

    private static PlannedTask TurnOff(IScheduledTaskWorker worker, string current, bool canPlan) => new()
    {
        Worker = worker,
        Cadence = Cadence.Off,
        CadenceLabel = CadenceLabel(Cadence.Off),
        CurrentSchedule = current,
        ProposedSchedule = ScheduleStorage.FormatTriggers(null),
        Reason = "You chose to leave it unscheduled. It only runs when you start it.",
        Triggers = Array.Empty<TaskTriggerInfo>(),
        Choice = ScheduleStorage.ChoiceOff,
        CanPlan = canPlan
    };

    private static PlannedTask LeaveAlone(IScheduledTaskWorker worker, string current, string reason)
    {
        string? warning = null;
        var fastest = worker.Triggers?
            .Where(t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks.HasValue)
            .Select(t => TimeSpan.FromTicks(t.IntervalTicks.GetValueOrDefault()))
            .OrderBy(t => t)
            .FirstOrDefault();

        if (fastest is { } interval && interval > TimeSpan.Zero && interval < TimeSpan.FromHours(1))
        {
            warning = $"Runs every {ScheduleStorage.FormatInterval(interval)}. Check it really needs to run that often.";
        }

        return new PlannedTask
        {
            Worker = worker,
            Cadence = Cadence.LeaveAlone,
            CadenceLabel = CadenceLabel(Cadence.LeaveAlone),
            CurrentSchedule = current,
            ProposedSchedule = current,
            Reason = reason,
            Warning = warning,
            Choice = ScheduleStorage.ChoiceKeep
        };
    }

    private static (double? Minutes, string Evidence) MeasureCost(IScheduledTaskWorker worker, ReliabilityRecord? rec)
    {
        // The last completed run counts too: libraries grow, so a recent long run beats an old average.
        var last = worker.LastExecutionResult;
        double? lastSeconds = last is not null && last.Status == TaskCompletionStatus.Completed
            ? Math.Max(0, (last.EndTimeUtc - last.StartTimeUtc).TotalSeconds)
            : null;

        if (rec is not null && rec.TotalRuns > 0)
        {
            string runs = rec.TotalRuns == 1 ? "1 recorded run" : $"{rec.TotalRuns} recorded runs";
            if (lastSeconds is { } l && l > rec.AverageDurationSeconds * 1.2)
            {
                return (l / 60, $"its last run took {ScheduleStorage.FormatDuration(l)}, longer than its average of {ScheduleStorage.FormatDuration(rec.AverageDurationSeconds)} over {runs}");
            }

            return (rec.AverageDurationSeconds / 60, $"averages {ScheduleStorage.FormatDuration(rec.AverageDurationSeconds)} over {runs}");
        }

        if (lastSeconds is { } seconds)
        {
            return (seconds / 60, $"its last run took {ScheduleStorage.FormatDuration(seconds)}");
        }

        return (null, "there's no run history yet");
    }

    private static Cadence ChooseCadence(TaskRule rule, double? minutes) => minutes switch
    {
        null => rule.Moderate,
        < CheapMinutes => rule.Cheap,
        >= HeavyMinutes => rule.Heavy,
        _ => rule.Moderate
    };

    private static string BuildReason(Candidate c)
    {
        bool costDecides = c.Rule.Cheap != c.Rule.Moderate || c.Rule.Moderate != c.Rule.Heavy;
        if (!costDecides)
        {
            return $"{c.Rule.Why} ({Capitalise(c.Evidence)}.)";
        }

        string because = c.Minutes switch
        {
            null => "so it starts at a middle setting until there's history to go on",
            < CheapMinutes => "so running it this often costs very little",
            >= HeavyMinutes => "so it's spread out to cut the night-time load",
            _ => "so it's spread out a little"
        };

        return $"{c.Rule.Why} {Capitalise(c.Evidence)}, {because}.";
    }

    private static List<string> CommonWarnings(Candidate c)
    {
        var warnings = new List<string>();
        if (c.Record is { TotalRuns: >= 3 } rec && rec.FailureRatePercent > 30)
        {
            warnings.Add($"Fails often ({rec.FailureRatePercent}% of recorded runs). See the Run history tab.");
        }

        if (c.Record?.OriginalTriggers is not null)
        {
            warnings.Add("Currently moved 3 hours later after a failure. Applying resets that.");
        }

        return warnings;
    }

    private static string? JoinWarnings(List<string> warnings) => warnings.Count == 0 ? null : string.Join(" ", warnings);

    private static string CadenceLabel(Cadence cadence) => cadence switch
    {
        Cadence.Daily => "Every day",
        Cadence.EveryOtherDay => "Every other day",
        Cadence.TwiceWeekly => "Every 3–4 days",
        Cadence.Weekly => "Once a week",
        Cadence.Monthly => "Once a month",
        Cadence.Off => "Not scheduled",
        _ => "Unchanged"
    };

    private static string Capitalise(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---------- Slot allocation ----------

    private static int BlockCells(double minutes)
    {
        double run = Math.Min(Math.Max(minutes, 0), MaxBlockMinutes);
        double total = run + Math.Max(BufferMinutes, run * BufferShare);
        return Math.Max(1, (int)Math.Ceiling(total / CellMinutes)); // a quick task takes one 15-minute slot
    }

    /// <summary>
    /// The quietest start that is free on every one of the given days. Cost is the expected
    /// number of people watching while the task runs, plus: a penalty for starting before the
    /// earlier stages have finished that night, a small nudge to follow them closely, a nudge
    /// towards days with less already planned, and (when costs are equal) the early hours.
    /// </summary>
    private static (int Start, double Cost)? BestCommonStart(bool[,] grid, BusyProfile busy, NightOrder night, DayOfWeek[] days, int length)
    {
        (int Start, double Cost)? best = null;
        for (int start = 0; start + length <= CellsPerDay; start++)
        {
            if (!days.All(d => IsFree(grid, d, start, length)))
            {
                continue;
            }

            double cost = 0;
            foreach (var day in days)
            {
                cost += busy.CostOver(day, start, length, CellMinutes);
                cost += night.Cost(day, start);

                cost += Load(grid, day) * SpreadNudge;
            }

            // Prefer the early hours (from 01:00) when everything else is equal.
            cost += ((start - EarlyHoursCell + CellsPerDay) % CellsPerDay) * TieBreakNudge;

            if (best is null || cost < best.Value.Cost)
            {
                best = (start, cost);
            }
        }

        return best;
    }

    private static bool IsFree(bool[,] grid, DayOfWeek day, int start, int length)
    {
        for (int cell = start; cell < Math.Min(start + length, CellsPerDay); cell++)
        {
            if (grid[(int)day, cell])
            {
                return false;
            }
        }

        return true;
    }

    private static void Mark(bool[,] grid, IEnumerable<DayOfWeek> days, int start, int length)
    {
        foreach (var day in days)
        {
            for (int cell = start; cell < Math.Min(start + length, CellsPerDay); cell++)
            {
                grid[(int)day, cell] = true;
            }
        }
    }

    private static int Load(bool[,] grid, DayOfWeek day)
    {
        int used = 0;
        for (int cell = 0; cell < CellsPerDay; cell++)
        {
            if (grid[(int)day, cell])
            {
                used++;
            }
        }

        return used;
    }

    private static void ReserveExisting(bool[,] grid, IEnumerable<TaskTriggerInfo>? triggers, double minutes)
    {
        if (triggers is null)
        {
            return;
        }

        int length = BlockCells(minutes);
        foreach (var t in triggers.Where(t => t.TimeOfDayTicks.HasValue))
        {
            int start = (int)(TimeSpan.FromTicks(t.TimeOfDayTicks.GetValueOrDefault()).TotalMinutes / CellMinutes);
            if (t.Type == TaskTriggerInfoType.DailyTrigger)
            {
                Mark(grid, AllDays, start, length);
            }
            else if (t.Type == TaskTriggerInfoType.WeeklyTrigger && t.DayOfWeek.HasValue)
            {
                Mark(grid, new[] { t.DayOfWeek.Value }, start, length);
            }
        }
    }

    // ---------- Triggers ----------

    // Keep any time limit already set on the task.
    private static long? MaxRuntime(IScheduledTaskWorker worker) =>
        worker.Triggers?.Select(t => t.MaxRuntimeTicks).FirstOrDefault(v => v != null);

    private static TaskTriggerInfo Daily(TimeSpan at, long? maxRuntime) => new()
    {
        Type = TaskTriggerInfoType.DailyTrigger,
        TimeOfDayTicks = at.Ticks,
        MaxRuntimeTicks = maxRuntime
    };

    private static TaskTriggerInfo Weekly(DayOfWeek day, TimeSpan at, long? maxRuntime) => new()
    {
        Type = TaskTriggerInfoType.WeeklyTrigger,
        DayOfWeek = day,
        TimeOfDayTicks = at.Ticks,
        MaxRuntimeTicks = maxRuntime
    };

    /// <summary>
    /// Keeps each night in stage order. A night runs from 12 hours before that day's library scan
    /// (or from noon when there's none), so "before" and "after" still make sense across midnight.
    /// </summary>
    private sealed class NightOrder
    {
        private readonly int[] _anchor = Enumerable.Repeat(-1, 7).ToArray(); // cell the night starts at
        private readonly int[] _earlierEnd = new int[7];   // end of the earlier stages, in cells after the anchor
        private readonly int[] _stageEnd = new int[7];     // end of the stage being placed now
        private int _stage = -1;

        public void BeginStage(int stage)
        {
            if (stage == _stage)
            {
                return;
            }

            for (int d = 0; d < 7; d++)
            {
                _earlierEnd[d] = Math.Max(_earlierEnd[d], _stageEnd[d]);
            }

            _stage = stage;
        }

        public void Placed(DayOfWeek day, int start, int length)
        {
            int d = (int)day;
            if (_anchor[d] < 0)
            {
                _anchor[d] = _stage == 0 ? (start - NoonCell + CellsPerDay) % CellsPerDay : NoonCell;
            }

            _stageEnd[d] = Math.Max(_stageEnd[d], Offset(d, start) + length);
        }

        public double Cost(DayOfWeek day, int start)
        {
            int d = (int)day;
            if (_anchor[d] < 0 || _earlierEnd[d] == 0)
            {
                return 0; // first stage of the night
            }

            int offset = Offset(d, start);
            return offset < _earlierEnd[d]
                ? BeforeEarlierStage
                : (offset - _earlierEnd[d]) * FollowNudge;
        }

        private int Offset(int d, int cell) => ((cell - (_anchor[d] < 0 ? NoonCell : _anchor[d])) % CellsPerDay + CellsPerDay) % CellsPerDay;
    }

    private sealed record TaskRule(
        string Keyword,
        Cadence Cheap,
        Cadence Moderate,
        Cadence Heavy,
        int Stage,
        double DefaultMinutes,
        string Why);

    private sealed record Candidate(
        IScheduledTaskWorker Worker,
        TaskRule Rule,
        ReliabilityRecord? Record,
        ManagedRun? Managed,
        string Current,
        double? Minutes,
        string Evidence);
}

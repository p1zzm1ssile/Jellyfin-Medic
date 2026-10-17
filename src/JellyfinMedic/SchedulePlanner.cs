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

    // Minutes after midnight, or -1 when the task isn't given a time (left alone, turned off, or no free slot).
    public int StartMinutes { get; set; } = -1;

    /// <summary>The owner's choice for this task: medic, keep or off.</summary>
    public string Choice { get; set; } = ScheduleStorage.ChoiceMedic;

    /// <summary>False for tasks Medic doesn't know how to plan, which can only be kept or turned off.</summary>
    public bool CanPlan { get; set; } = true;

    public bool Changes => Cadence != Cadence.LeaveAlone && CurrentSchedule != ProposedSchedule;
}

/// <summary>
/// A task that has no time to run at: what it does, and whether Medic recommends giving it one.
/// </summary>
public sealed class UntimedTaskAdvice
{
    public const string On = "On";
    public const string Off = "Off";
    public const string YourCall = "Your call";

    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string WhatItDoes { get; set; } = string.Empty;

    // "Never runs on its own" or "Only when Jellyfin starts".
    public string Now { get; set; } = string.Empty;

    // On, Off or Your call.
    public string Recommendation { get; set; } = YourCall;

    public string Why { get; set; } = string.Empty;

    // How often Medic would run it, when it recommends turning it on.
    public string? HowOften { get; set; }
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
/// no two planned tasks overlap. A block can run on past midnight into the next day, and the
/// tasks you keep and the times you asked to avoid are blocked out the same way. Each night
/// runs in stages: the library scan first, then the tasks that build on it, then upkeep. A
/// task is only put before an earlier stage when every slot after it is much busier. Tasks
/// that don't run every night are spread across nights.
///
/// Once Medic has learned when the server is busy, hours it has never seen the server on are left
/// out, and a task keeps its current time unless another saves a clear amount (MoveSaving).
/// </summary>
public static class SchedulePlanner
{
    private const int CellMinutes = WeekGrid.CellMinutes;
    private const int CellsPerDay = WeekGrid.CellsPerDay;
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

    // What a move to another time has to save, once Medic has learned how busy the server is, so
    // near ties don't make a task hop between times from one week to the next. A score is in people
    // watching x hours summed over the runs of one week; a monthly task's single run counts 12/52 of it.
    private const double MoveSaving = 0.25;
    private const double MonthlyRunsPerWeek = 12.0 / 52;

    private const string NoQuieterTime = "There was no quieter free time for it.";
    private const string NotWorthMoving = "A slightly quieter time is free, but it would save too little to be worth moving it.";
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

    // Jellyfin's own tasks are matched by key, which is the same in every language (the name is translated);
    // tasks from plugins, which have no key here, by name, where the first match wins, so more specific names
    // come first. Cheap / Moderate / Heavy: the cadence to use when the task's average run is
    // under 5 minutes / 5 to 45 minutes / 45 minutes or more.
    private static readonly TaskRule[] Rules =
    {
        new("RefreshLibrary", "Scan Media Library", Cadence.Daily, Cadence.Daily, Cadence.Daily, 0, 60,
            "New IPTV and local titles should appear by morning, and later tasks need an up-to-date library, so it runs daily and first."),
        new("RefreshGuide", "Guide", Cadence.Daily, Cadence.Daily, Cadence.Daily, 1, 20,
            "The IPTV guide changes every day. It gets its own slot after the library scan, so their database writes never overlap."),
        new("DeleteTranscodeFiles", "Transcode", Cadence.Daily, Cadence.Daily, Cadence.Daily, 1, 5,
            "Clears leftover transcode files so the cache drive doesn't fill up."),
        new("DownloadSubtitles", "Subtitle", Cadence.TwiceWeekly, Cadence.TwiceWeekly, Cadence.TwiceWeekly, 2, 20,
            "Searching less often keeps you under subtitle provider limits."),
        new("RefreshChapterImages", "Chapter Image", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("RefreshTrickplayImages", "Trickplay", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("TaskExtractMediaSegments", "Media Segment", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new("KeyframeExtraction", "Keyframe", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added items, so batching them up loses nothing."),
        new(null, "Intro", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 30,
            "Only works on newly added episodes, so batching them up loses nothing."),
        new("AudioNormalization", "Audio Normali", Cadence.Daily, Cadence.EveryOtherDay, Cadence.TwiceWeekly, 2, 15,
            "Only works on newly added music, so batching it up loses nothing."),
        new("OptimizeDatabaseTask", "Optimize", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 15,
            "Keeps the database tidy. Weekly is enough and avoids extra writes to the cache drive."),
        new("DeleteCacheFiles", "Cache Directory", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 5,
            "Clears old cached images and files. Weekly is plenty."),
        new("CleanLogFiles", "Log Directory", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 5,
            "Removes old log files. Weekly is plenty."),
        new("PluginUpdates", "Update Plugins", Cadence.Weekly, Cadence.Weekly, Cadence.Weekly, 3, 5,
            "Weekly keeps plugins current without surprise changes mid-week."),
        new("RefreshPeople", "People", Cadence.Weekly, Cadence.Weekly, Cadence.Monthly, 4, 60,
            "Refreshes cast and crew details, which rarely change."),
        new("CleanActivityLog", "Activity Log", Cadence.Monthly, Cadence.Monthly, Cadence.Monthly, 4, 5,
            "Only trims old entries from the activity log.")
    };

    private static TaskRule? RuleFor(IScheduledTaskWorker worker)
    {
        string key = worker.ScheduledTask.Key;
        return Rules.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal))
            ?? Rules.FirstOrDefault(r => worker.Name.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static List<PlannedTask> Build(
        IReadOnlyList<IScheduledTaskWorker> workers,
        IReadOnlyDictionary<string, ReliabilityRecord> profile,
        IReadOnlyDictionary<string, ManagedRun> managed,
        BusyProfile busy,
        IReadOnlyDictionary<string, string>? choices = null)
    {
        var grid = new WeekGrid();
        busy.BlockAvoidedTimes(grid);

        var planned = new List<PlannedTask>();
        var leftAlone = new List<PlannedTask>();
        var candidates = new List<Candidate>();
        var night = new NightOrder();

        foreach (var worker in workers)
        {
            string id = worker.Id.ToString();
            profile.TryGetValue(id, out var rec);
            managed.TryGetValue(id, out var managedRun);

            var rule = RuleFor(worker);
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
                ReserveExisting(grid, worker.Triggers, managedRun, minutes ?? LeftAloneDefaultMinutes);
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

        // In the order the night runs (noon to noon), so 23:00 comes before 01:00. Tasks that found no free
        // slot have no time, so they go after the rest.
        return planned
            .OrderBy(p => p.StartMinutes < 0)
            .ThenBy(p => (p.StartMinutes - (NoonCell * CellMinutes) + (24 * 60)) % (24 * 60))
            .ThenBy(p => p.Worker.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(leftAlone.OrderBy(p => p.Worker.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static PlannedTask Place(WeekGrid grid, BusyProfile busy, NightOrder night, Candidate c, Cadence cadence)
    {
        double estimate = c.Minutes ?? c.Rule.DefaultMinutes;
        int length = BlockCells(estimate);
        long? maxRuntime = MaxRuntime(c.Worker);

        // Each day the task runs, with the cell it starts at.
        var slots = ChooseSlots(grid, busy, night, cadence, length, allowUnseen: false);
        if (slots.Count == 0)
        {
            return LeaveAlone(c.Worker, c.Current,
                busy.HasUnseenTimes && ChooseSlots(grid, busy, night, cadence, length, allowUnseen: true).Count > 0
                    ? "The only free times left are hours Medic has never seen the server on, so it may be switched off then. It's left as it is."
                    : "There was no free slot left for this task, so it's left as it is.");
        }

        // Before Medic has learned anything the profile doesn't change from week to week, so there's nothing to damp.
        // Slots the task could have moved to, when it stays where it is instead.
        List<(DayOfWeek Day, int Start)>? passedOver = null;
        if ((busy.FromViewing || busy.FromServerLoad)
            && CurrentSlots(c, cadence) is { } current
            && current.All(sl => grid.IsFree(sl.Day, sl.Start, length) && !busy.TouchesUnseen(sl.Start, length))
            && (SlotsCost(grid, busy, night, current, length) - SlotsCost(grid, busy, night, slots, length))
                * (cadence == Cadence.Monthly ? MonthlyRunsPerWeek : 1) < MoveSaving)
        {
            passedOver = slots;
            slots = current;
        }

        foreach (var (day, start) in slots)
        {
            grid.Mark(day, start, length);
            night.Placed(day, start, length);
        }

        var warnings = CommonWarnings(c);
        int runMinutes = (int)Math.Ceiling(estimate);
        if (busy.FromViewing)
        {
            var (peak, peakMinute) = Busiest(slots, sl => busy.PeakOver(sl.Day, sl.Start * CellMinutes, runMinutes));
            if (peak >= BusyThreshold)
            {
                string why = passedOver is not null && Busiest(passedOver, sl => busy.PeakOver(sl.Day, sl.Start * CellMinutes, runMinutes)).Peak < peak
                    ? NotWorthMoving
                    : NoQuieterTime;
                warnings.Insert(0, $"Runs when people often watch (about {peak:0.#} watching around {ScheduleStorage.Hhmm(TimeSpan.FromMinutes(peakMinute))}). {why}");
            }
        }

        if (busy.FromServerLoad)
        {
            var (peak, peakMinute) = Busiest(slots, sl => busy.ServerPeakOver(sl.Day, sl.Start * CellMinutes, runMinutes));
            if (peak >= BusyProfile.BusyServerThreshold)
            {
                string why = passedOver is not null && Busiest(passedOver, sl => busy.ServerPeakOver(sl.Day, sl.Start * CellMinutes, runMinutes)).Peak < peak
                    ? NotWorthMoving
                    : NoQuieterTime;
                warnings.Add($"Runs when the server is usually busy (about {peak * 100:0}% around {ScheduleStorage.Hhmm(TimeSpan.FromMinutes(peakMinute))}). {why}");
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
        else if (passedOver is not null)
        {
            // Staying put: rewriting the same times in another form would show as a change in Preview.
            result.Triggers = c.Worker.Triggers.ToArray();
            result.ProposedSchedule = ScheduleStorage.FormatTriggers(result.Triggers);
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

    // The days the task runs and the cell it starts at on each, or none when nothing is free.
    private static List<(DayOfWeek Day, int Start)> ChooseSlots(WeekGrid grid, BusyProfile busy, NightOrder night, Cadence cadence, int length, bool allowUnseen)
    {
        var slots = new List<(DayOfWeek Day, int Start)>();

        if (cadence == Cadence.Daily)
        {
            // One time every day is simplest. Only use different times on different days when
            // that is clearly quieter (your quiet hours differ through the week).
            var same = BestCommonStart(grid, busy, night, AllDays, length, allowUnseen);
            var perDay = AllDays.Select(d => (Day: d, Best: BestCommonStart(grid, busy, night, new[] { d }, length, allowUnseen))).ToList();
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

            return slots;
        }

        (DayOfWeek[] Days, int Start, double Cost)? best = null;
        foreach (var days in DaySets(cadence))
        {
            if (BestCommonStart(grid, busy, night, days, length, allowUnseen) is { } found &&
                (best is null || found.Cost < best.Value.Cost))
            {
                best = (days, found.Start, found.Cost);
            }
        }

        if (best is { } b)
        {
            slots.AddRange(b.Days.Select(d => (d, b.Start)));
        }

        return slots;
    }

    private static (double Peak, int Minute) Busiest(List<(DayOfWeek Day, int Start)> slots, Func<(DayOfWeek Day, int Start), (double Peak, int Minute)> peakOf) =>
        slots.Select(peakOf).OrderByDescending(x => x.Peak).First();

    private static DayOfWeek[][] DaySets(Cadence cadence) => cadence switch
    {
        Cadence.EveryOtherDay => EveryOtherDaySets,
        Cadence.TwiceWeekly => TwiceWeeklySets,
        _ => WeeklySets // Weekly, and Monthly (which runs on the first of a chosen weekday)
    };

    /// <summary>
    /// Where the task runs now, as planner slots, when that is a schedule the planner could have
    /// chosen for this cadence; null otherwise (another cadence, or times off the quarter hour).
    /// </summary>
    private static List<(DayOfWeek Day, int Start)>? CurrentSlots(Candidate c, Cadence cadence)
    {
        if (cadence == Cadence.Monthly)
        {
            return c.Managed is { } m && StartCell(m.TimeOfDayTicks) is { } monthlyCell
                ? new List<(DayOfWeek Day, int Start)> { (m.Weekday, monthlyCell) }
                : null;
        }

        if (c.Managed is not null)
        {
            return null;
        }

        var slots = new List<(DayOfWeek Day, int Start)>();
        foreach (var t in c.Worker.Triggers ?? Enumerable.Empty<TaskTriggerInfo>())
        {
            if (t.TimeOfDayTicks is not { } ticks || StartCell(ticks) is not { } cell)
            {
                return null;
            }

            if (t.Type == TaskTriggerInfoType.DailyTrigger)
            {
                slots.AddRange(AllDays.Select(d => (d, cell)));
            }
            else if (t.Type == TaskTriggerInfoType.WeeklyTrigger && t.DayOfWeek is { } day)
            {
                slots.Add((day, cell));
            }
            else
            {
                return null;
            }
        }

        var days = slots.Select(sl => sl.Day).ToHashSet();
        if (days.Count != slots.Count)
        {
            return null; // more than one run on a day
        }

        bool fits = cadence == Cadence.Daily
            ? days.Count == AllDays.Length
            : DaySets(cadence).Any(set => days.SetEquals(set)) && slots.Select(sl => sl.Start).Distinct().Count() == 1;
        return fits ? slots : null;
    }

    private static int? StartCell(long ticks)
    {
        var at = TimeSpan.FromTicks(ticks);
        return at >= TimeSpan.Zero && at < TimeSpan.FromDays(1) && ticks % TimeSpan.FromMinutes(CellMinutes).Ticks == 0
            ? (int)(at.TotalMinutes / CellMinutes)
            : null;
    }

    private static double SlotsCost(WeekGrid grid, BusyProfile busy, NightOrder night, List<(DayOfWeek Day, int Start)> slots, int length) =>
        slots.Sum(sl => SlotCost(grid, busy, night, sl.Day, sl.Start, length));

    /// <summary>
    /// The tasks with no time to run at (no daily, weekly or interval trigger, and not run monthly by Medic),
    /// with what each does and whether Medic recommends turning it on.
    /// </summary>
    public static List<UntimedTaskAdvice> Untimed(
        IReadOnlyList<IScheduledTaskWorker> workers,
        IReadOnlyDictionary<string, ReliabilityRecord> profile,
        IReadOnlyDictionary<string, ManagedRun> managed)
    {
        var result = new List<UntimedTaskAdvice>();
        foreach (var worker in workers)
        {
            string id = worker.Id.ToString();
            var triggers = worker.Triggers ?? Array.Empty<TaskTriggerInfo>();
            if (managed.ContainsKey(id) || triggers.Any(t => t.Type is TaskTriggerInfoType.DailyTrigger or TaskTriggerInfoType.WeeklyTrigger or TaskTriggerInfoType.IntervalTrigger))
            {
                continue;
            }

            var advice = new UntimedTaskAdvice
            {
                TaskId = id,
                TaskName = worker.Name,
                Category = worker.Category ?? string.Empty,
                WhatItDoes = string.IsNullOrWhiteSpace(worker.Description) ? "Its maker gives no description." : worker.Description.Trim(),
                Now = triggers.Any(t => t.Type == TaskTriggerInfoType.StartupTrigger) ? "Only when Jellyfin starts" : "Never runs on its own"
            };

            string key = worker.ScheduledTask.Key;
            if (key == RestartForUpdatesTask.TaskKey)
            {
                advice.Recommendation = UntimedTaskAdvice.On;
                advice.HowOften = CadenceLabel(Cadence.Weekly);
                advice.Why = "Plugin updates only finish installing after a restart, and this waits until nobody is watching. Give it a time after Update Plugins, such as 04:00 once a week.";
            }
            else if (key == ScheduledRestartTask.TaskKey)
            {
                advice.Recommendation = UntimedTaskAdvice.Off;
                advice.Why = "Jellyfin doesn't need regular restarts. Only turn it on if your server gets slow or uses more memory the longer it runs.";
            }
            else if (RuleFor(worker) is { } rule)
            {
                profile.TryGetValue(id, out var rec);
                var (minutes, _) = MeasureCost(worker, rec);
                advice.Recommendation = UntimedTaskAdvice.On;
                advice.HowOften = CadenceLabel(ChooseCadence(rule, minutes));
                advice.Why = rule.Why + " Choose \"Let Medic schedule it\" in Preview changes to give it a quiet time.";
            }
            else
            {
                advice.Why = "Medic doesn't know this task, so it has no advice. Leave it off unless you use what it does; if you do, give it a time under Scheduled Tasks.";
            }

            result.Add(advice);
        }

        return result
            .OrderBy(a => a.Recommendation switch { UntimedTaskAdvice.On => 0, UntimedTaskAdvice.YourCall => 1, _ => 2 })
            .ThenBy(a => a.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    // The run time plus a buffer, since runs vary.
    private static double BlockMinutes(double minutes)
    {
        double run = Math.Min(Math.Max(minutes, 0), MaxBlockMinutes);
        return run + Math.Max(BufferMinutes, run * BufferShare);
    }

    private static int BlockCells(double minutes) =>
        Math.Max(1, (int)Math.Ceiling(BlockMinutes(minutes) / CellMinutes)); // a quick task takes one 15-minute slot

    /// <summary>
    /// The quietest start that is free on every one of the given days. Cost is the expected
    /// number of people watching while the task runs, plus: a penalty for starting before the
    /// earlier stages have finished that night, a small nudge to follow them closely, a nudge
    /// towards days with less already planned, and (when costs are equal) the early hours.
    /// </summary>
    private static (int Start, double Cost)? BestCommonStart(WeekGrid grid, BusyProfile busy, NightOrder night, DayOfWeek[] days, int length, bool allowUnseen)
    {
        (int Start, double Cost)? best = null;

        // Any start time of the day: a run that doesn't end by midnight carries on into the next day.
        for (int start = 0; start < CellsPerDay; start++)
        {
            if (!days.All(d => grid.IsFree(d, start, length)) || (!allowUnseen && busy.TouchesUnseen(start, length)))
            {
                continue;
            }

            double cost = days.Sum(day => SlotCost(grid, busy, night, day, start, length));

            // Prefer the early hours (from 01:00) when everything else is equal.
            cost += ((start - EarlyHoursCell + CellsPerDay) % CellsPerDay) * TieBreakNudge;

            if (best is null || cost < best.Value.Cost)
            {
                best = (start, cost);
            }
        }

        return best;
    }

    private static double SlotCost(WeekGrid grid, BusyProfile busy, NightOrder night, DayOfWeek day, int start, int length) =>
        busy.CostOver(day, start, length, CellMinutes) + night.Cost(day, start) + (grid.Load(day) * SpreadNudge);

    private static void ReserveExisting(WeekGrid grid, IEnumerable<TaskTriggerInfo>? triggers, ManagedRun? managed, double minutes)
    {
        foreach (var t in triggers ?? Enumerable.Empty<TaskTriggerInfo>())
        {
            if (t.TimeOfDayTicks is not { } ticks)
            {
                continue;
            }

            if (t.Type == TaskTriggerInfoType.DailyTrigger)
            {
                foreach (var day in AllDays)
                {
                    Reserve(grid, day, TimeSpan.FromTicks(ticks), minutes);
                }
            }
            else if (t.Type == TaskTriggerInfoType.WeeklyTrigger && t.DayOfWeek is { } weekday)
            {
                Reserve(grid, weekday, TimeSpan.FromTicks(ticks), minutes);
            }
        }

        // A kept task that Medic runs once a month keeps that time too.
        if (managed is not null)
        {
            Reserve(grid, managed.Weekday, TimeSpan.FromTicks(managed.TimeOfDayTicks), minutes);
        }
    }

    // From the cell the run starts in to the one its buffer ends in: a 10-minute run at 23:50 also takes 00:00 to 00:15.
    private static void Reserve(WeekGrid grid, DayOfWeek day, TimeSpan at, double minutes)
    {
        double start = at.TotalMinutes;
        int first = (int)(start / CellMinutes);
        int end = (int)Math.Ceiling((start + BlockMinutes(minutes)) / CellMinutes);
        grid.Mark(day, first, Math.Max(1, end - first));
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
        string? Key,
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

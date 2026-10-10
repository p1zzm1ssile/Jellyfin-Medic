using MediaBrowser.Model.Tasks;

namespace JellyfinMedic.Api;

public class TaskBackupContainer
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public Dictionary<string, TaskTriggerInfo[]> TriggersByTaskId { get; set; } = new();

    // Monthly tasks run by Medic at the time of the backup.
    // Null in backups made before monthly scheduling existed.
    public List<ManagedRun>? ManagedRuns { get; set; }
}

/// <summary>
/// A task that Medic runs itself, because Jellyfin has no monthly trigger.
/// It runs on the first of the chosen weekday each month, for example the first Thursday.
/// </summary>
public class ManagedRun
{
    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public DayOfWeek Weekday { get; set; } = DayOfWeek.Sunday;

    public long TimeOfDayTicks { get; set; }

    // Server local time of the last run Medic started (or when the task was handed to it).
    public DateTime? LastStartedLocal { get; set; }
}

public class ReliabilityRecord
{
    public string TaskKey { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public int TotalRuns { get; set; }

    public int Failures { get; set; }

    public double FailureRatePercent => TotalRuns > 0 ? Math.Round(((double)Failures / TotalRuns) * 100, 1) : 0;

    public double AverageDurationSeconds { get; set; }

    public bool RequiresExclusiveExecution { get; set; }

    // Set only while a back-off is in effect: the schedule to put back after the next successful run.
    public TaskTriggerInfo[]? OriginalTriggers { get; set; }

    public DateTime? BackedOffSinceUtc { get; set; }
}

public class MovementAudit
{
    public DateTime Timestamp { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public string PreviousTime { get; set; } = string.Empty;

    public string NewTime { get; set; } = string.Empty;

    public string Rationale { get; set; } = string.Empty;
}

/// <summary>
/// One finished run of a scheduled task, as recorded by the listener.
/// </summary>
public class RunRecord
{
    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public string? ErrorDetails { get; set; }

    public double DurationSeconds => Math.Max(0, (EndUtc - StartUtc).TotalSeconds);
}

public class BackupFileInfo
{
    public string FileName { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

public class CalendarRun
{
    public string Time { get; set; } = string.Empty;

    public int SortMinutes { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public string Schedule { get; set; } = string.Empty;

    // normal, interval, managed (run by Medic) or backoff (moved after a failure)
    public string Kind { get; set; } = "normal";

    public bool LastFailed { get; set; }

    // For the timeline: start (minutes after midnight, -1 for "many times a day"), typical length,
    // colour group (library, analysis, upkeep, monthly, plugin) and the details panel text.
    public int StartMinutes { get; set; }

    public double DurationMinutes { get; set; }

    public string TaskId { get; set; } = string.Empty;

    public string Category { get; set; } = "upkeep";

    public string AverageText { get; set; } = string.Empty;

    public string LastText { get; set; } = string.Empty;

    public string? Note { get; set; }
}

/// <summary>The owner's choice for one task in Preview (see ScheduleStorage.ChoiceMedic and friends).</summary>
public class TaskChoice
{
    public string TaskId { get; set; } = string.Empty;

    public string Choice { get; set; } = string.Empty;
}

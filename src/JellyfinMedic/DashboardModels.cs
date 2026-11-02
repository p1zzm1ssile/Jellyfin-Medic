using JellyfinMedic.Services;

namespace JellyfinMedic.Api;

/// <summary>
/// The Dashboard's slower data, put together from what Medic already records. Built on request and kept
/// for a minute. Every minute, hour and day in it is server local time.
/// </summary>
public sealed class DashboardData
{
    public DateTime GeneratedUtc { get; set; }

    // The server's offset from UTC when this was built.
    public int UtcOffsetMinutes { get; set; }

    // Section name to a short message, for each section that couldn't be built; that section is null.
    public Dictionary<string, string> Errors { get; set; } = new(StringComparer.Ordinal);

    public BusyHoursSection? BusyHours { get; set; }

    public TodaySection? Today { get; set; }

    public TranscodesSection? Transcodes { get; set; }

    public DisksSection? Disks { get; set; }

    public NetworkSection? Network { get; set; }

    public List<RepeatedError>? RepeatedErrors { get; set; }

    public PeopleSection? People { get; set; }

    public List<string>? Updates { get; set; }
}

/// <summary>What's happening now, cheap enough to poll every 15 seconds.</summary>
public sealed class DashboardLive
{
    public NowSnapshot Now { get; set; } = new();

    // About the last 10 minutes of readings, one every 15 seconds, oldest first.
    public List<ResourceSample> Resources { get; set; } = new();

    // The levels the resource monitor treats as heavy use.
    public ResourceLimits Limits { get; set; } = new();

    // Tasks LoadGuard stopped and will restart once memory recovers, oldest first.
    public List<PausedTask> Paused { get; set; } = new();
}

public sealed class ResourceLimits
{
    public double CpuPercent { get; set; }

    public double MemoryPercent { get; set; }

    public double DiskMBps { get; set; }

    public double GpuPercent { get; set; }
}

/// <summary>A task LoadGuard stopped to protect the server, waiting to be started again.</summary>
public sealed class PausedTask
{
    public string TaskId { get; init; } = string.Empty;

    public string TaskName { get; init; } = string.Empty;

    public DateTime StoppedUtc { get; init; }
}

public sealed class BusyHoursSection
{
    // False until 72 hours of viewing are recorded; until then no hour counts as busy.
    public bool Ready { get; set; }

    public double HoursRecorded { get; set; }

    // 7 days, Monday first, of 24 hours each.
    public List<List<BusyHour>> Days { get; set; } = new();

    // Task runs in the last 7 days that started in a busy hour.
    public int RunsInBusyHours { get; set; }
}

public sealed class BusyHour
{
    public double AverageStreams { get; set; }

    public double AverageTranscodes { get; set; }

    // People usually watch at this hour.
    public bool Busy { get; set; }

    // Task runs that started in this hour in the last 7 days, most first.
    public List<HourRuns> Runs { get; set; } = new();
}

public sealed class HourRuns
{
    public string TaskName { get; set; } = string.Empty;

    public int Count { get; set; }
}

public sealed class TodaySection
{
    // yyyy-MM-dd
    public string Date { get; set; } = string.Empty;

    public int NowMinutes { get; set; }

    public List<PlannedRun> Planned { get; set; } = new();

    public List<ActualRun> Actual { get; set; } = new();

    public List<SpikeMark> Spikes { get; set; } = new();

    public List<GuardMark> Guard { get; set; } = new();

    public TodaySummary Summary { get; set; } = new();
}

public sealed class PlannedRun
{
    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public int StartMinutes { get; set; }

    public double DurationMinutes { get; set; }

    // library, analysis, upkeep, monthly or plugin, as on the Schedule tab.
    public string Category { get; set; } = string.Empty;

    // normal, interval, managed or backoff, as on the Schedule tab.
    public string Kind { get; set; } = string.Empty;

    // ran, late, cancelled (it had only cancelled or aborted runs), not run or waiting.
    public string Status { get; set; } = string.Empty;
}

public sealed class ActualRun
{
    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public int StartMinutes { get; set; }

    public int EndMinutes { get; set; }

    // Started before today and was still going at midnight: StartMinutes is then 0, not its real start.
    public bool StartedBeforeToday { get; set; }

    // Completed, Failed, Cancelled or Aborted, as Jellyfin reports it, or Running for a run that hasn't finished.
    public string Status { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
}

public sealed class SpikeMark
{
    public int Minute { get; set; }

    // CPU, Memory, Disk or GPU.
    public string Resource { get; set; } = string.Empty;

    public string Level { get; set; } = string.Empty;
}

public sealed class GuardMark
{
    public int Minute { get; set; }

    // stopped or restarted.
    public string Action { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;
}

public sealed class TodaySummary
{
    public int Planned { get; set; }

    // Every run that started today or was still going at midnight, finished or still going.
    public int Runs { get; set; }

    public int Running { get; set; }

    public int Completed { get; set; }

    public int Failed { get; set; }

    // Runs Jellyfin reports as Aborted.
    public int Stopped { get; set; }

    public int Cancelled { get; set; }

    public int NotRun { get; set; }

    public int Late { get; set; }
}

public sealed class TranscodesSection
{
    public DateTime? Since { get; set; }

    public int Samples { get; set; }

    public List<TranscodeReasonShare> Reasons { get; set; } = new();

    // Up to 5 titles, most transcoded first.
    public List<TranscodeTitle> Top { get; set; } = new();

    // When the media report was built; null when there is none.
    public DateTime? MediaReportUtc { get; set; }
}

// Not "TranscodeReason": Jellyfin has a type of that name, and its API document names schemas by short name.
public sealed class TranscodeReasonShare
{
    // As Jellyfin reports it, e.g. AudioCodecNotSupported.
    public string Reason { get; set; } = string.Empty;

    // Share of all samples with this reason, 0 to 100. A sample can have several reasons.
    public double Share { get; set; }

    // What in the library causes it, named after the media report's count: DtsOrTrueHdOnly, ImageSubtitles,
    // OldContainers or Hdr. Null for reasons the library doesn't explain.
    public string? LibraryCause { get; set; }

    // Files with that cause in the latest media report, for the whole server. Null without a cause or a report.
    public int? LibraryCount { get; set; }
}

public sealed class TranscodeTitle
{
    public string Title { get; set; } = string.Empty;

    public int Samples { get; set; }

    // Most frequent first.
    public List<string> Reasons { get; set; } = new();
}

public sealed class DisksSection
{
    // Soonest full first.
    public List<DiskOutlook> Drives { get; set; } = new();

    // The days Medic freed space, oldest first: from the weekly ledger, not per drive.
    public List<FreedDay> Freed { get; set; } = new();
}

public sealed class DiskOutlook
{
    public string Key { get; set; } = string.Empty;

    // What the drive holds, e.g. "Films, Series". Empty until a reading records it.
    public string Holds { get; set; } = string.Empty;

    public double FreeGb { get; set; }

    // 0 when the drive can't be read now.
    public double TotalGb { get; set; }

    // The library folder it was read through no longer exists (a drive that didn't mount, say); FreeGb is then the last reading.
    public bool Missing { get; set; }

    // That folder, when Missing.
    public string Path { get; set; } = string.Empty;

    // Null until the readings span 3 days.
    public double? GbPerDay { get; set; }

    // Null when the drive isn't filling.
    public double? DaysToFull { get; set; }

    // One reading a day, oldest first.
    public List<DiskPoint> Series { get; set; } = new();
}

public sealed class DiskPoint
{
    // yyyy-MM-dd
    public string Day { get; set; } = string.Empty;

    public double FreeGb { get; set; }
}

public sealed class FreedDay
{
    // yyyy-MM-dd
    public string Day { get; set; } = string.Empty;

    public long Bytes { get; set; }

    // What freed it, e.g. "Duplicate files" or "Track cleanup".
    public List<string> Kinds { get; set; } = new();
}

public sealed class NetworkSection
{
    // The latest speed test; null when none was run.
    public SpeedTestSummary? Test { get; set; }

    // The remote streaming limit set now; 0 means none.
    public double CurrentLimitMbps { get; set; }

    // The most streams seen at once, at home and away together.
    public int MostStreams { get; set; }

    // The speed test's option for that many viewers; null without a test.
    public SpeedOption? Suggested { get; set; }
}

public sealed class SpeedTestSummary
{
    public DateTime TestedUtc { get; set; }

    public double DownloadMbps { get; set; }

    public double UploadMbps { get; set; }

    public double LatencyMs { get; set; }

    public int IptvHdStreams { get; set; }
}

public sealed class PeopleSection
{
    public int Total { get; set; }

    public List<string> AdminNames { get; set; } = new();

    // "name (last seen 3 Oct 2026)" or "name (never signed in)", as on the Users check.
    public List<string> Inactive { get; set; } = new();

    public List<string> NoPassword { get; set; } = new();

    // Accounts that signed in from 3 or more places in 7 days, most first. Null when the activity log
    // couldn't be read.
    public List<RoamingAccount>? Roaming { get; set; }
}

public sealed class RoamingAccount
{
    public string Name { get; set; } = string.Empty;

    // How many addresses; never which.
    public int Addresses { get; set; }
}

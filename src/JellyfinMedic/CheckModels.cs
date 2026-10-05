using System;
using System.Collections.Generic;
using System.Linq;
using JellyfinMedic.Services;

namespace JellyfinMedic.Api;

/// <summary>How serious a finding is. Kept as strings so the page gets readable names.</summary>
public static class Sev
{
    public const string Problem = "Problem";
    public const string Improve = "Improve";
    public const string Tip = "Tip";
    public const string Good = "Good";

    public static int Rank(string severity) => severity switch
    {
        Problem => 0,
        Improve => 1,
        Tip => 2,
        _ => 3
    };
}

/// <summary>One piece of advice: what was found, what to change it to, why, and where.</summary>
public class Finding
{
    public string Area { get; set; } = string.Empty;

    public string Severity { get; set; } = Sev.Tip;

    public string Title { get; set; } = string.Empty;

    public string Current { get; set; } = string.Empty;

    public string Recommended { get; set; } = string.Empty;

    public string Why { get; set; } = string.Empty;

    public string Where { get; set; } = string.Empty;

    // The Jellyfin setting this is about, as "Section|Name" matching the settings list (empty if none).
    public string Setting { get; set; } = string.Empty;

    // Identifies the finding so the admin can choose to ignore it.
    public string Key { get; set; } = string.Empty;

    public bool Ignored { get; set; }
}

public class SpecItem
{
    public string Group { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public class SettingRow
{
    public string Section { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public class PluginReport
{
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? ConfigFile { get; set; }

    public List<SettingRow> Settings { get; set; } = new();

    public List<Finding> Findings { get; set; } = new();
}

public class DiagnosticReport
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public List<SpecItem> Specs { get; set; } = new();

    public List<Finding> Findings { get; set; } = new();

    // For the Dashboard tiles.
    public UserSummary? Users { get; set; }

    public List<string> Updates { get; set; } = new();

    public List<RepeatedError> RepeatedErrors { get; set; } = new();
}

/// <summary>Medic's own settings, as shown and saved on the Settings tab.</summary>
public class MedicSettingsDto
{
    public bool AvoidEnabled { get; set; }

    public int AvoidStartHour { get; set; } = 18;

    public int AvoidEndHour { get; set; } = 23;

    public int InactiveUserDays { get; set; } = 90;

    public bool LoadGuardEnabled { get; set; } = true;

    public int MemoryCeilingPercent { get; set; } = 85;

    public string ScheduleMode { get; set; } = "suggest";

    public string TracksKeepLanguages { get; set; } = "eng";

    public bool TracksRemoveUndetermined { get; set; }

    public bool TracksRemoveUntaggedSubtitles { get; set; }

    public bool TracksKeepFirstUntaggedSubtitle { get; set; } = true;

    public bool TracksAllowRemovingOnlySubtitle { get; set; }

    public bool TracksWindowEnabled { get; set; }

    public int TracksWindowStartHour { get; set; } = 1;

    public int TracksWindowEndHour { get; set; } = 7;

    public bool TracksPauseWhileWatching { get; set; } = true;

    public bool TracksReplaceInPlace { get; set; }

    public int TracksConcurrentFiles { get; set; } = 1;

    public int TracksFfmpegThreads { get; set; } = 1;
}

/// <summary>Playback seen in one hour of the week (server local time).</summary>
public class UsageBucket
{
    public int Samples { get; set; }

    public long StreamSum { get; set; }

    public long TranscodeSum { get; set; }

    public int MaxStreams { get; set; }

    public int MaxTranscodes { get; set; }
}

/// <summary>
/// What the usage sampler has recorded. Buckets are 7 x 24: index = day * 24 + hour,
/// with day 0 = Monday, in server local time.
/// </summary>
public class UsageProfile
{
    public DateTime? FirstSampleUtc { get; set; }

    public DateTime? LastSampleUtc { get; set; }

    public int TotalSamples { get; set; }

    public List<UsageBucket> Buckets { get; set; } = Enumerable.Range(0, 168).Select(_ => new UsageBucket()).ToList();
}

public class UsageSummary
{
    public double HoursCollected { get; set; }

    public bool Ready { get; set; }

    // Average number of people watching, 168 values, Monday 00:00 first.
    public List<double> AverageStreams { get; set; } = new();

    // How many 5-minute samples each of those hours has; 0 means that hour hasn't been seen yet.
    public List<int> SampleCounts { get; set; } = new();

    public string? BusiestHours { get; set; }

    public string? BusiestDay { get; set; }

    public string? QuietWindow { get; set; }

    public int QuietStartHour { get; set; } = 1;

    public double? TranscodeShare { get; set; }

    public int MaxStreams { get; set; }

    public int MaxTranscodes { get; set; }

    // Average streams between 01:00 and 06:00, when Medic runs heavy tasks.
    public double NightAverage { get; set; }
}

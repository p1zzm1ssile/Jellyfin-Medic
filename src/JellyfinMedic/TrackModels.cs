namespace JellyfinMedic.Services;

// What the track cleanup reports: its plans, scans and run progress (see TrackCleaner).

public class TrackInfo
{
    public int Index { get; set; }

    public string Kind { get; set; } = string.Empty;   // audio | subtitle

    public string Language { get; set; } = "und";

    public string Codec { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Channels { get; set; } = string.Empty;

    public bool Forced { get; set; }

    public bool Default { get; set; }

    public bool Keep { get; set; } = true;

    public string KeepReason { get; set; } = string.Empty;

    // A separate subtitle file beside the film (never changed by a remux).
    public bool External { get; set; }

    public string? ExternalPath { get; set; }

    // The language as stored in the file, before any language you've set by hand.
    public string FileLanguage { get; set; } = "und";

    // You've set this track's language by hand; it's written into the file on the next run.
    public bool Overridden { get; set; }

    // Bits per second, when Jellyfin knows it (used to estimate the track's size).
    public long? BitRate { get; set; }
}

public class FilePlan
{
    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Library { get; set; } = string.Empty;

    public List<TrackInfo> Tracks { get; set; } = new();

    // The film or episode's name, for lists.
    public string Title { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public int RemovingAudio { get; set; }

    public int RemovingSubtitles { get; set; }

    // Tracks whose language you've set by hand and that still need writing into the file.
    public int TagsToWrite { get; set; }

    // Rough space freed by the tracks being removed.
    public long SavingBytes { get; set; }

    public bool WouldChange => RemovingAudio + RemovingSubtitles + TagsToWrite > 0;

    public bool HasStrippedCopy { get; set; }

    // File size when the run started; remuxing time tracks size far better than file count.
    public long SizeBytes { get; set; }

    public string? Error { get; set; }
}

public class UndeterminedGroup
{
    public string Kind { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public string Channels { get; set; } = string.Empty;

    // Files that have at least one track in this group (a file counts once, however many it has).
    public int Files { get; set; }

    // Every untagged track in this group, across all files.
    public int Tracks { get; set; }

    public List<string> Examples { get; set; } = new();
}

public class TrackScanStatus
{
    public bool Running { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public TrackScanResult? Result { get; set; }

    public string? Error { get; set; }
}

/// <summary>One track with no language, for the paged list.</summary>
public class UntaggedTrack
{
    public string Title { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long FileBytes { get; set; }

    public int Index { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Codec { get; set; } = string.Empty;

    public string Channels { get; set; } = string.Empty;

    public bool External { get; set; }

    public string? ExternalPath { get; set; }

    // The name Jellyfin will recognise for a separate subtitle file, e.g. "Film (2009).eng.srt".
    public string? SuggestedName { get; set; }

    public long? TrackBytes { get; set; }

    // Language you've set by hand, if any.
    public string? SetLanguage { get; set; }

    public bool WillBeRemoved { get; set; }

    // It's the film's only audio track (inside the file), so it's almost certainly the film's own language.
    public bool OnlyAudioTrack { get; set; }

    // The film looks like anime (genre or an "anime" folder), where untagged audio is often Japanese.
    public bool Anime { get; set; }
}

public class PagedList<T>
{
    public int Total { get; set; }

    public int Page { get; set; }

    public int Pages { get; set; }

    public List<T> Items { get; set; } = new();
}

public class TrackScanResult
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    public int FilesScanned { get; set; }

    public int FilesWouldChange { get; set; }

    public int AudioToRemove { get; set; }

    public int SubtitlesToRemove { get; set; }

    public List<UndeterminedGroup> Undetermined { get; set; } = new();

    public List<FilePlan> Sample { get; set; } = new(); // first N changing files, for preview

    public int UntaggedCount { get; set; }

    // Files have been changed since this scan, so it may be out of date.
    public bool Stale { get; set; }

    // Full lists, served a page at a time (left out of the summary to keep it small).
    [System.Text.Json.Serialization.JsonIgnore]
    public List<FilePlan> Changing { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<UntaggedTrack> Untagged { get; set; } = new();

    public string? Error { get; set; }
}

public class TrackRunProgress
{
    public bool Running { get; set; }

    public bool DryRun { get; set; } = true;

    public int Total { get; set; }

    public int Done { get; set; }

    public int Changed { get; set; }

    public int Failed { get; set; }

    // Left alone on purpose (out of space, linked, changed meanwhile...): not failures.
    public int Skipped { get; set; }

    public long BytesSaved { get; set; }

    // Size of the originals this run moved into .medic-originals: space still held until they're deleted.
    public long BytesKept { get; set; }

    public string? Current { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    // Size of every file in this run, and of the files finished so far.
    public long BytesTotal { get; set; }

    public long BytesDone { get; set; }

    // When remuxing began (after the file list was built).
    public DateTime? WorkStartedUtc { get; set; }

    // Estimated seconds left, from the rate data has been processed so far. Null until there's enough to go on.
    public int? SecondsLeft { get; set; }

    // Paused by the user (files already started still finish).
    public bool Paused { get; set; }

    // Why the run is holding off right now, e.g. "Paused" or "Waiting for 01:00–07:00". Null while working.
    public string? Waiting { get; set; }

    // Time spent paused or waiting, left out of the time-left estimate.
    public double WaitedSeconds { get; set; }

    public DateTime? WaitingSinceUtc { get; set; }

    public List<string> Recent { get; set; } = new();
}

/// <summary>A title "Tidy copies" left as it was: its file name, and why its earlier copy didn't check out.</summary>
public sealed record TidyLeft(string Name, string Reason);

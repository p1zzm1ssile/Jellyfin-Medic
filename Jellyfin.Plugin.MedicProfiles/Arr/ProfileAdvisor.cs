using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

public class ProfileSummary
{
    public string App { get; set; } = "radarr";

    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> Allowed { get; set; } = new();

    public string? Cutoff { get; set; }

    public bool UpgradeAllowed { get; set; }

    public int MinFormatScore { get; set; }

    /// <summary>Custom formats with a score other than 0, as "Name: score".</summary>
    public List<string> Scores { get; set; } = new();

    /// <summary>How many films or series use it. Null when that couldn't be read.</summary>
    public int? InUse { get; set; }
}

public class Suggestion
{
    public string App { get; set; } = "radarr";

    /// <summary>problem, improve or tip.</summary>
    public string Severity { get; set; } = "tip";

    public string Title { get; set; } = string.Empty;

    /// <summary>Which profiles it applies to.</summary>
    public List<string> Profiles { get; set; } = new();

    /// <summary>What to change, in Sonarr or Radarr's own words.</summary>
    public string Change { get; set; } = string.Empty;

    public string Why { get; set; } = string.Empty;

    /// <summary>What on this server led to it, e.g. "12 files transcoded because the TV can't play DTS".</summary>
    public string? Evidence { get; set; }
}

public class PlaybackEvidence
{
    /// <summary>Medic's transcode log was found.</summary>
    public bool FromMedic { get; set; }

    public DateTime? SinceUtc { get; set; }

    public int TranscodedFiles { get; set; }

    public int SampledFiles { get; set; }

    public Dictionary<string, int> Signals { get; set; } = new(StringComparer.Ordinal);
}

public class AdviceReport
{
    public DateTime GeneratedUtc { get; set; }

    public PlaybackEvidence Evidence { get; set; } = new();

    public List<ProfileSummary> Profiles { get; set; } = new();

    public List<Suggestion> Suggestions { get; set; } = new();

    public Dictionary<string, string> Errors { get; set; } = new();
}

/// <summary>
/// Quality profile advice from what this server actually plays. Reads Sonarr and Radarr's quality
/// profiles and custom formats, Medic's record of which files get transcoded and why, and a sample of
/// the library, and suggests changes so downloads are files your devices play directly.
/// Read-only: nothing is changed in Sonarr or Radarr.
/// </summary>
public class ProfileAdvisor
{
    private const int LibrarySample = 1500;
    private const int MinFiles = 3;

    // Qualities nobody wants by accident.
    private static readonly HashSet<string> JunkQualities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown", "WORKPRINT", "CAM", "TELESYNC", "TELECINE", "REGIONAL", "DVDSCR"
    };

    private readonly ArrClient _arr;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ProfileAdvisor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdviceReport? _cached;

    public ProfileAdvisor(ArrClient arr, ILibraryManager libraryManager, IApplicationPaths paths, ILogger<ProfileAdvisor> logger)
    {
        _arr = arr;
        _libraryManager = libraryManager;
        _paths = paths;
        _logger = logger;
    }

    public async Task<AdviceReport> GetAsync(bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _cached is not null && DateTime.UtcNow - _cached.GeneratedUtc < TimeSpan.FromHours(1))
            {
                return _cached;
            }

            var report = new AdviceReport { GeneratedUtc = DateTime.UtcNow, Evidence = GatherEvidence() };
            foreach (var app in new[] { ArrApp.Sonarr, ArrApp.Radarr })
            {
                if (!_arr.IsConfigured(app))
                {
                    continue;
                }

                var data = await ReadAppAsync(app, ct).ConfigureAwait(false);
                if (data.Error is not null)
                {
                    report.Errors[DownloadsService.Key(app)] = data.Error;
                    continue;
                }

                report.Profiles.AddRange(data.Profiles);
                report.Suggestions.AddRange(Advise(app, data, report.Evidence));
            }

            report.Suggestions = report.Suggestions
                .OrderBy(s => s.Severity == "problem" ? 0 : s.Severity == "improve" ? 1 : 2)
                .ThenBy(s => s.App)
                .ToList();
            _cached = report;
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- What this server plays ----------

    /// <summary>
    /// Signals, counted in files:
    /// dts       transcoded for audio, and its only audio is DTS or TrueHD
    /// hevc      transcoded because the device can't play HEVC
    /// av1       transcoded because the device can't play AV1
    /// dvonly    transcoded for HDR, and it's Dolby Vision with no HDR10 fallback
    /// hdr       transcoded for HDR (any kind)
    /// bitrate   transcoded because the bitrate was too high
    /// uhd       transcoded because 4K was too big for the device
    /// libHevc / libFiles / libDtsOnly / libUhd   from a sample of the library
    /// </summary>
    internal PlaybackEvidence GatherEvidence()
    {
        var evidence = new PlaybackEvidence();
        var signals = evidence.Signals;
        string logPath = Path.Combine(_paths.PluginConfigurationsPath, "JellyfinMedic", "transcode_log.json");
        try
        {
            if (File.Exists(logPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(logPath));
                var root = doc.RootElement;
                evidence.FromMedic = true;
                evidence.SinceUtc = Json.Date(root, "SinceUtc") ?? Json.Date(root, "sinceUtc");
                if ((root.TryGetProperty("Items", out var items) || root.TryGetProperty("items", out items)) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in items.EnumerateArray())
                    {
                        var reasons = Reasons(entry);
                        if (reasons.Count == 0)
                        {
                            continue;
                        }

                        evidence.TranscodedFiles++;
                        string? id = Json.Text(entry, "ItemId") ?? Json.Text(entry, "itemId");
                        var file = Guid.TryParse(id, out var itemId) ? Describe(_libraryManager.GetItemById(itemId)) : null;
                        Count(signals, reasons, file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: couldn't read Jellyfin Medic's transcode log");
        }

        SampleLibrary(evidence);
        return evidence;
    }

    private static HashSet<string> Reasons(JsonElement entry)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if ((entry.TryGetProperty("Reasons", out var r) || entry.TryGetProperty("reasons", out r)) && r.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in r.EnumerateObject())
            {
                set.Add(p.Name);
            }
        }

        return set;
    }

    internal static void Count(Dictionary<string, int> signals, HashSet<string> reasons, FileFacts? file)
    {
        void Add(string key) => signals[key] = signals.GetValueOrDefault(key) + 1;

        if (reasons.Contains("ContainerBitrateExceedsLimit") || reasons.Contains("VideoBitrateNotSupported"))
        {
            Add("bitrate");
        }

        if (file is null)
        {
            return;
        }

        if ((reasons.Contains("AudioCodecNotSupported") || reasons.Contains("AudioProfileNotSupported") || reasons.Contains("AudioChannelsNotSupported"))
            && file.LosslessOnlyAudio)
        {
            Add("dts");
        }

        if (reasons.Contains("VideoCodecNotSupported") || reasons.Contains("VideoProfileNotSupported") || reasons.Contains("VideoCodecTagNotSupported"))
        {
            if (file.VideoCodec == "hevc")
            {
                Add("hevc");
            }
            else if (file.VideoCodec == "av1")
            {
                Add("av1");
            }
        }

        if (reasons.Contains("VideoRangeTypeNotSupported"))
        {
            Add("hdr");
            if (file.DolbyVisionOnly)
            {
                Add("dvonly");
            }
        }

        if (reasons.Contains("VideoResolutionNotSupported") && file.Height > 1100)
        {
            Add("uhd");
        }
    }

    private void SampleLibrary(PlaybackEvidence evidence)
    {
        try
        {
            // Ids are cheap to list; a random sample of them keeps this quick on big libraries.
            var ids = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false
            });
            var sample = ids.Count <= LibrarySample ? ids : ids.OrderBy(_ => Random.Shared.Next()).Take(LibrarySample).ToList();

            foreach (var id in sample)
            {
                var file = Describe(_libraryManager.GetItemById(id));
                if (file is null || file.VideoCodec is null)
                {
                    continue;
                }

                evidence.SampledFiles++;
                var s = evidence.Signals;
                s["libFiles"] = s.GetValueOrDefault("libFiles") + 1;
                if (file.VideoCodec == "hevc")
                {
                    s["libHevc"] = s.GetValueOrDefault("libHevc") + 1;
                }

                if (file.LosslessOnlyAudio)
                {
                    s["libDtsOnly"] = s.GetValueOrDefault("libDtsOnly") + 1;
                }

                if (file.Height > 1100)
                {
                    s["libUhd"] = s.GetValueOrDefault("libUhd") + 1;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: couldn't sample the library");
        }
    }

    internal sealed class FileFacts
    {
        public string? VideoCodec { get; set; }

        public int Height { get; set; }

        /// <summary>Every audio track is DTS (any kind) or TrueHD, so devices without them must transcode.</summary>
        public bool LosslessOnlyAudio { get; set; }

        /// <summary>Dolby Vision with no HDR10 fallback layer.</summary>
        public bool DolbyVisionOnly { get; set; }
    }

    private static FileFacts? Describe(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var streams = item.GetMediaStreams();
        if (streams is null || streams.Count == 0)
        {
            return null;
        }

        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        string range = video?.VideoRangeType.ToString() ?? string.Empty;
        return new FileFacts
        {
            VideoCodec = video?.Codec?.ToLowerInvariant() switch
            {
                "h265" or "hevc" => "hevc",
                { } c => c,
                null => null
            },
            Height = video?.Height ?? 0,
            LosslessOnlyAudio = audio.Count > 0 && audio.All(a => a.Codec is { } c
                && (c.StartsWith("dts", StringComparison.OrdinalIgnoreCase) || c.Equals("truehd", StringComparison.OrdinalIgnoreCase))),
            DolbyVisionOnly = range.Equals("DOVI", StringComparison.OrdinalIgnoreCase)
        };
    }

    // ---------- What Sonarr and Radarr are set to ----------

    internal sealed class AppData
    {
        public List<ProfileSummary> Profiles { get; set; } = new();

        /// <summary>Profile id → custom format name → score.</summary>
        public Dictionary<int, Dictionary<string, int>> Scores { get; set; } = new();

        public List<string> CustomFormats { get; set; } = new();

        public string? Error { get; set; }
    }

    private async Task<AppData> ReadAppAsync(ArrApp app, CancellationToken ct)
    {
        var data = new AppData();
        using (var profiles = await _arr.GetAsync(app, "qualityprofile", ct).ConfigureAwait(false))
        {
            if (!profiles.Ok || profiles.Json is null || profiles.Json.RootElement.ValueKind != JsonValueKind.Array)
            {
                data.Error = profiles.Ok ? ArrClient.Name(app) + " sent something unexpected." : profiles.Message;
                return data;
            }

            ReadProfiles(app, profiles.Json.RootElement, data);
        }

        using (var formats = await _arr.GetAsync(app, "customformat", ct).ConfigureAwait(false))
        {
            if (formats.Ok && formats.Json is not null && formats.Json.RootElement.ValueKind == JsonValueKind.Array)
            {
                data.CustomFormats = formats.Json.RootElement.EnumerateArray()
                    .Select(f => Json.Text(f, "name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList();
            }
        }

        // Which profiles are used, from the film or series list. Optional: a big library is fine, a failure just leaves it blank.
        using (var media = await _arr.GetAsync(app, app == ArrApp.Radarr ? "movie" : "series", ct).ConfigureAwait(false))
        {
            if (media.Ok && media.Json is not null && media.Json.RootElement.ValueKind == JsonValueKind.Array)
            {
                var counts = media.Json.RootElement.EnumerateArray()
                    .Select(m => Json.Int(m, "qualityProfileId") ?? 0)
                    .GroupBy(id => id)
                    .ToDictionary(g => g.Key, g => g.Count());
                foreach (var p in data.Profiles)
                {
                    p.InUse = counts.GetValueOrDefault(p.Id);
                }
            }
        }

        return data;
    }

    internal static void ReadProfiles(ArrApp app, JsonElement list, AppData data)
    {
        foreach (var p in list.EnumerateArray())
        {
            var summary = new ProfileSummary
            {
                App = DownloadsService.Key(app),
                Id = Json.Int(p, "id") ?? 0,
                Name = Json.Text(p, "name") ?? "Unnamed",
                UpgradeAllowed = Json.Bool(p, "upgradeAllowed"),
                MinFormatScore = Json.Int(p, "minFormatScore") ?? 0
            };

            int cutoff = Json.Int(p, "cutoff") ?? -1;
            if (p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in Flatten(items))
                {
                    if (item.Allowed)
                    {
                        summary.Allowed.Add(item.Name);
                    }

                    if (item.Id == cutoff)
                    {
                        summary.Cutoff = item.Name;
                    }
                }

                // A group can be the cutoff too.
                summary.Cutoff ??= items.EnumerateArray().Where(i => Json.Int(i, "id") == cutoff).Select(i => Json.Text(i, "name")).FirstOrDefault();
            }

            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (p.TryGetProperty("formatItems", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in formats.EnumerateArray())
                {
                    string? name = Json.Text(f, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        scores[name] = Json.Int(f, "score") ?? 0;
                    }
                }
            }

            summary.Scores = scores.Where(s => s.Value != 0).OrderByDescending(s => s.Value)
                .Select(s => s.Key + ": " + s.Value.ToString("+0;-0", CultureInfo.InvariantCulture)).ToList();
            data.Scores[summary.Id] = scores;
            data.Profiles.Add(summary);
        }
    }

    private static IEnumerable<(int Id, string Name, bool Allowed)> Flatten(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            bool allowed = Json.Bool(item, "allowed");
            if (item.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object)
            {
                yield return (Json.Int(q, "id") ?? -1, Json.Text(q, "name") ?? "?", allowed);
            }
            else if (item.TryGetProperty("items", out var inner) && inner.ValueKind == JsonValueKind.Array)
            {
                // A group: its qualities share the group's tick.
                foreach (var child in Flatten(inner))
                {
                    yield return (child.Id, child.Name, allowed);
                }
            }
        }
    }

    // ---------- Advice ----------

    internal static List<Suggestion> Advise(ArrApp app, AppData data, PlaybackEvidence evidence)
    {
        var list = new List<Suggestion>();
        string appKey = DownloadsService.Key(app);
        string appName = ArrClient.Name(app);
        var s = evidence.Signals;
        int Sig(string k) => s.GetValueOrDefault(k);
        var active = data.Profiles.Where(p => p.InUse is null or > 0).ToList();

        // Profiles where a custom format matching the pattern is missing or scored 0 or higher.
        List<string> NotPenalised(Regex pattern, out string? format)
        {
            format = data.CustomFormats.FirstOrDefault(f => pattern.IsMatch(f));
            string? name = format;
            return active.Where(p => name is null || data.Scores.GetValueOrDefault(p.Id)?.GetValueOrDefault(name) >= 0)
                .Select(p => p.Name).ToList();
        }

        Suggestion Penalise(string title, Regex pattern, string formatHint, string why, string evidenceText, string severity)
        {
            var profiles = NotPenalised(pattern, out string? format);
            return new Suggestion
            {
                App = appKey,
                Severity = severity,
                Title = title,
                Profiles = profiles,
                Change = format is not null
                    ? $"In {appName} → Settings → Profiles, give the custom format \"{format}\" a negative score (for example −10000 to refuse it, or −100 to only prefer others) in these profiles."
                    : $"{appName} has no custom format for this. Add one (TRaSH Guides has \"{formatHint}\": Settings → Custom Formats → +, or import its JSON), then give it a negative score in these profiles.",
                Why = why,
                Evidence = evidenceText
            };
        }

        // ----- From playback (needs Jellyfin Medic's transcode log) -----
        if (Sig("dts") >= MinFiles)
        {
            var sug = Penalise(
                "Avoid releases whose only audio is DTS or TrueHD",
                new Regex(@"\b(dts|truehd)\b", RegexOptions.IgnoreCase),
                "DTS / TrueHD audio formats",
                "Some of your devices can't play DTS or TrueHD, so Jellyfin converts the audio every time those files play. Releases that also carry AC3 or EAC3 audio play directly.",
                Files(Sig("dts")) + " transcoded because the device couldn't play their DTS or TrueHD audio.",
                "improve");
            if (sug.Profiles.Count > 0)
            {
                list.Add(sug);
            }
        }

        if (Sig("hevc") >= MinFiles)
        {
            var sug = Penalise(
                "Prefer H.264 over HEVC (x265)",
                new Regex(@"(x265|hevc|h\.?265)", RegexOptions.IgnoreCase),
                "x265 (HD)",
                "Some of your devices can't play HEVC, so those files are converted on the fly. That costs CPU or GPU time and can buffer.",
                Files(Sig("hevc")) + " transcoded because the device couldn't play HEVC.",
                "improve");
            if (sug.Profiles.Count > 0)
            {
                list.Add(sug);
            }
        }
        else if (Sig("libFiles") >= 100 && Sig("libHevc") * 5 >= Sig("libFiles") && evidence.FromMedic && Sig("hevc") == 0)
        {
            list.Add(new Suggestion
            {
                App = appKey,
                Severity = "tip",
                Title = "HEVC plays fine here, which saves space",
                Profiles = active.Select(p => p.Name).ToList(),
                Change = "If disk space matters, you can let x265 releases through (score them 0 rather than negative), keeping a release-group custom format so poor re-encodes are still avoided.",
                Why = "Your HEVC files aren't being transcoded, so your devices play them directly. HEVC is often 30–50% smaller for the same picture. TRaSH Guides discourage x265 at 1080p mainly because many such releases are low-quality re-encodes, not for playback reasons.",
                Evidence = $"{Pct(Sig("libHevc"), Sig("libFiles"))}% of sampled files are HEVC, and none were caught being transcoded for it."
            });
        }

        if (Sig("av1") >= MinFiles)
        {
            var sug = Penalise(
                "Avoid AV1 releases",
                new Regex(@"\bav1\b", RegexOptions.IgnoreCase),
                "AV1",
                "Few TVs and streaming sticks decode AV1 in hardware, so these files are converted every time.",
                Files(Sig("av1")) + " transcoded because the device couldn't play AV1.",
                "improve");
            if (sug.Profiles.Count > 0)
            {
                list.Add(sug);
            }
        }

        if (Sig("dvonly") >= MinFiles)
        {
            var sug = Penalise(
                "Avoid Dolby Vision without an HDR10 fallback",
                new Regex(@"(\bdv\b|dolby ?vision|dovi)", RegexOptions.IgnoreCase),
                "DV (w/o HDR fallback)",
                "Dolby Vision files with no HDR10 layer only look right on Dolby Vision screens. Everywhere else Jellyfin has to tone-map them, which is heavy work.",
                Files(Sig("dvonly")) + " transcoded because they're Dolby Vision only.",
                "improve");
            if (sug.Profiles.Count > 0)
            {
                list.Add(sug);
            }
        }
        else if (Sig("hdr") >= MinFiles * 2)
        {
            list.Add(new Suggestion
            {
                App = appKey,
                Severity = "tip",
                Title = "Most of your screens don't show HDR",
                Profiles = active.Where(p => p.Allowed.Any(a => a.Contains("2160", StringComparison.Ordinal))).Select(p => p.Name).ToList(),
                Change = "Keep 4K HDR in a separate profile for the titles you'll watch on an HDR screen, and use a 1080p SDR profile for everything else.",
                Why = "HDR files played on SDR screens have to be tone-mapped, which is the heaviest kind of transcode.",
                Evidence = Files(Sig("hdr")) + " transcoded because the screen couldn't show their HDR."
            });
        }

        if (Sig("uhd") >= MinFiles)
        {
            var uhdProfiles = active.Where(p => p.Allowed.Any(a => a.Contains("2160", StringComparison.Ordinal))).Select(p => p.Name).ToList();
            if (uhdProfiles.Count > 0)
            {
                list.Add(new Suggestion
                {
                    App = appKey,
                    Severity = "improve",
                    Title = "4K is being shrunk for devices that can't show it",
                    Profiles = uhdProfiles,
                    Change = "Untick the 2160p qualities in these profiles, or move them to a separate 4K profile used only for titles you'll watch on a 4K screen.",
                    Why = "4K files are four times the pixels of 1080p. On a 1080p device Jellyfin has to scale them down live, and they take far more disk space.",
                    Evidence = Files(Sig("uhd")) + " transcoded because the device couldn't play 4K."
                });
            }
        }

        if (Sig("bitrate") >= MinFiles)
        {
            var remux = active.Where(p => p.Allowed.Any(a => a.Contains("Remux", StringComparison.OrdinalIgnoreCase))).Select(p => p.Name).ToList();
            if (remux.Count > 0)
            {
                list.Add(new Suggestion
                {
                    App = appKey,
                    Severity = "improve",
                    Title = "Remux files are too big to stream to some viewers",
                    Profiles = remux,
                    Change = "Untick Remux qualities in these profiles (Bluray or WEB-DL of the same resolution looks nearly identical at a fraction of the size), or keep Remux only in a profile for titles watched at home.",
                    Why = "Remux files run at 20–80 Mbps. Over the internet, or on weaker Wi-Fi, Jellyfin has to cut the bitrate by transcoding.",
                    Evidence = Files(Sig("bitrate")) + " transcoded because the bitrate was too high for the connection or device."
                });
            }
        }

        // ----- From the profiles themselves (no playback data needed) -----
        foreach (var p in data.Profiles)
        {
            var junk = p.Allowed.Where(a => JunkQualities.Contains(a)).ToList();
            if (junk.Count > 0)
            {
                list.Add(new Suggestion
                {
                    App = appKey,
                    Severity = "problem",
                    Title = "Cinema recordings and unknown quality are allowed",
                    Profiles = new List<string> { p.Name },
                    Change = "Untick " + string.Join(", ", junk) + " in this profile.",
                    Why = "These are camera recordings from cinemas, pre-release copies, or files whose quality couldn't be told. They're almost always poor, and they take the place of a proper release.",
                    Evidence = null
                });
            }

            var scores = data.Scores.GetValueOrDefault(p.Id) ?? new Dictionary<string, int>();
            int best = scores.Values.Where(v => v > 0).Sum();
            if (p.MinFormatScore > 0 && best < p.MinFormatScore)
            {
                list.Add(new Suggestion
                {
                    App = appKey,
                    Severity = "problem",
                    Title = "Nothing can reach this profile's minimum custom format score",
                    Profiles = new List<string> { p.Name },
                    Change = string.Format(CultureInfo.InvariantCulture, "Lower \"Minimum Custom Format Score\" from {0} to {1} or below, or give more custom formats a positive score.", p.MinFormatScore, best),
                    Why = "A release has to score at least the minimum to be grabbed. Even a release matching every positive custom format here only scores " + best.ToString(CultureInfo.InvariantCulture) + ", so nothing will ever download.",
                    Evidence = null
                });
            }
        }

        var unused = data.CustomFormats.Where(f => data.Scores.Values.All(sc => sc.GetValueOrDefault(f) == 0)).ToList();
        if (unused.Count > 0 && data.Profiles.Count > 0)
        {
            list.Add(new Suggestion
            {
                App = appKey,
                Severity = "tip",
                Title = unused.Count == 1 ? "A custom format does nothing" : unused.Count + " custom formats do nothing",
                Profiles = new List<string>(),
                Change = "Give " + Names(unused) + " a score in the profiles where it should matter, or delete " + (unused.Count == 1 ? "it" : "them") + ".",
                Why = "A custom format only affects downloads through its score in a profile. Scored 0 everywhere, it's just shown in the release name.",
                Evidence = null
            });
        }

        var idle = data.Profiles.Where(p => p.InUse == 0).Select(p => p.Name).ToList();
        if (idle.Count > 0 && idle.Count < data.Profiles.Count)
        {
            list.Add(new Suggestion
            {
                App = appKey,
                Severity = "tip",
                Title = idle.Count == 1 ? "A profile isn't used" : idle.Count + " profiles aren't used",
                Profiles = idle,
                Change = "Delete them, or leave them if you keep them for the odd title. Fewer profiles are easier to keep right.",
                Why = "No " + (app == ArrApp.Radarr ? "film" : "series") + " in " + appName + " uses " + (idle.Count == 1 ? "it" : "them") + ".",
                Evidence = null
            });
        }

        return list;
    }

    private static string Files(int n) => n == 1 ? "1 file was" : n.ToString(CultureInfo.InvariantCulture) + " files were";

    private static int Pct(int part, int whole) => whole == 0 ? 0 : (int)Math.Round(100.0 * part / whole);

    private static string Names(List<string> names) =>
        names.Count <= 3 ? string.Join(", ", names.Select(n => "\"" + n + "\"")) : string.Join(", ", names.Take(3).Select(n => "\"" + n + "\"")) + " and " + (names.Count - 3) + " more";
}

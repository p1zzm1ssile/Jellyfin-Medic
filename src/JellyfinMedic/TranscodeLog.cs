using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace JellyfinMedic.Services;

public sealed class TranscodeEntry
{
    public string ItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    // Number of 5-minute checks that caught this file being transcoded.
    public int Samples { get; set; }

    // Why Jellyfin transcoded it, as reported by Jellyfin (e.g. AudioCodecNotSupported), with counts.
    public Dictionary<string, int> Reasons { get; set; } = new(StringComparer.Ordinal);

    // Apps that needed the transcode (e.g. "Jellyfin Web", "Android TV").
    public List<string> Clients { get; set; } = new();

    public DateTime LastSeenUtc { get; set; }
}

public sealed class TranscodeLogData
{
    public DateTime? SinceUtc { get; set; }

    public List<TranscodeEntry> Items { get; set; } = new();
}

public sealed class TranscodeLogView
{
    public DateTime? SinceUtc { get; set; }

    public int Files { get; set; }

    public int Samples { get; set; }

    public Dictionary<string, int> ReasonTotals { get; set; } = new(StringComparer.Ordinal);

    public List<TranscodeEntry> Top { get; set; } = new();
}

/// <summary>
/// Remembers which files get transcoded and why, from the same 5-minute playback check that builds
/// the usage profile. Stores titles and reasons only; nothing about who was watching.
/// </summary>
public static class TranscodeLog
{
    private const int MaxItems = 300;
    private static readonly object Sync = new();

    private static string FilePath(IApplicationPaths paths) =>
        Path.Combine(paths.PluginConfigurationsPath, "JellyfinMedic", "transcode_log.json");

    /// <summary>Records one check. Sessions are Jellyfin session objects that are currently playing.</summary>
    public static void Record(IApplicationPaths paths, IEnumerable<object> playingSessions)
    {
        lock (Sync)
        {
            var data = Load(paths);
            bool changed = false;
            if (data.SinceUtc is null)
            {
                data.SinceUtc = DateTime.UtcNow;
                changed = true;
            }

            foreach (var s in playingSessions)
            {
                if (!string.Equals(SettingsReader.Text(s, "PlayState.PlayMethod"), "Transcode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var item = SettingsReader.Get(s, "NowPlayingItem");
                string id = SettingsReader.Text(item, "Id") ?? string.Empty;
                if (id.Length == 0)
                {
                    continue;
                }

                var entry = data.Items.FirstOrDefault(e => e.ItemId == id);
                if (entry is null)
                {
                    entry = new TranscodeEntry { ItemId = id, Title = Title(item) };
                    data.Items.Add(entry);
                }

                entry.Samples++;
                entry.LastSeenUtc = DateTime.UtcNow;

                string reasons = SettingsReader.Get(s, "TranscodingInfo.TranscodeReasons")?.ToString() ?? string.Empty;
                var list = reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(r => r != "0")
                    .ToList();
                if (list.Count == 0)
                {
                    list.Add("Unknown");
                }

                foreach (var r in list)
                {
                    entry.Reasons[r] = entry.Reasons.GetValueOrDefault(r) + 1;
                }

                string client = SettingsReader.Text(s, "Client") ?? string.Empty;
                if (client.Length > 0 && !entry.Clients.Contains(client) && entry.Clients.Count < 6)
                {
                    entry.Clients.Add(client);
                }

                changed = true;
            }

            if (!changed)
            {
                return;
            }

            if (data.Items.Count > MaxItems)
            {
                data.Items = data.Items.OrderByDescending(e => e.LastSeenUtc).Take(MaxItems).ToList();
            }

            try
            {
                string file = FilePath(paths);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                JellyfinMedic.Api.ScheduleStorage.WriteText(file, JsonSerializer.Serialize(data));
            }
            catch
            {
                // Try again at the next check.
            }
        }
    }

    public static TranscodeLogView View(IApplicationPaths paths)
    {
        lock (Sync)
        {
            var data = Load(paths);
            var view = new TranscodeLogView
            {
                SinceUtc = data.SinceUtc,
                Files = data.Items.Count,
                Samples = data.Items.Sum(e => e.Samples),
                Top = data.Items.OrderByDescending(e => e.Samples).ThenByDescending(e => e.LastSeenUtc).Take(30).ToList()
            };

            foreach (var e in data.Items)
            {
                foreach (var r in e.Reasons)
                {
                    view.ReasonTotals[r.Key] = view.ReasonTotals.GetValueOrDefault(r.Key) + r.Value;
                }
            }

            return view;
        }
    }

    private static TranscodeLogData Load(IApplicationPaths paths)
    {
        try
        {
            string file = FilePath(paths);
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<TranscodeLogData>(File.ReadAllText(file)) ?? new TranscodeLogData();
            }
        }
        catch
        {
            // Start afresh if the file is damaged.
        }

        return new TranscodeLogData();
    }

    private static string Title(object? item)
    {
        string name = SettingsReader.Text(item, "Name") ?? "Unknown";
        string? series = SettingsReader.Text(item, "SeriesName");
        long? season = SettingsReader.Number(item, "ParentIndexNumber");
        long? episode = SettingsReader.Number(item, "IndexNumber");
        if (!string.IsNullOrWhiteSpace(series) && season is not null && episode is not null)
        {
            return $"{series} S{season:00}E{episode:00}";
        }

        long? year = SettingsReader.Number(item, "ProductionYear");
        return year is { } y ? $"{name} ({y})" : name;
    }
}

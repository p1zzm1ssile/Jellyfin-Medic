using System.Text.Json;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>What one person has already seen: the last "What's new" version, and the banners they dismissed.</summary>
public class SeenState
{
    public string UserId { get; set; } = string.Empty;

    public string? SeenVersion { get; set; }

    public List<string> DismissedAlerts { get; set; } = new();
}

/// <summary>
/// Remembers, per person and on the server, which "What's new" and which banners they've already seen,
/// so each shows once in all, not once on every phone, browser and TV they sign in on.
/// </summary>
public static class SeenStore
{
    private static readonly object Sync = new();

    private static string FilePath(string pluginConfigDir) => Path.Combine(pluginConfigDir, "JellyfinMedic", "seen.json");

    public static SeenState Get(string pluginConfigDir, string userId)
    {
        lock (Sync)
        {
            return ScheduleStorage.ReadList<SeenState>(FilePath(pluginConfigDir)).FirstOrDefault(s => SameUser(s.UserId, userId))
                ?? new SeenState { UserId = userId };
        }
    }

    public static void Update(string pluginConfigDir, string userId, Action<SeenState> change)
    {
        lock (Sync)
        {
            string path = FilePath(pluginConfigDir);
            var all = ScheduleStorage.ReadList<SeenState>(path);
            var mine = all.FirstOrDefault(s => SameUser(s.UserId, userId));
            if (mine is null)
            {
                mine = new SeenState { UserId = userId };
                all.Add(mine);
            }

            change(mine);
            mine.DismissedAlerts = mine.DismissedAlerts.Distinct(StringComparer.Ordinal).TakeLast(100).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ScheduleStorage.WriteJson(path, all);
        }
    }

    private static bool SameUser(string a, string b) =>
        Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

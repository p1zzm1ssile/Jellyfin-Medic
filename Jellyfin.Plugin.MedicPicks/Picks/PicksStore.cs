using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Medic.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Stores picks as one JSON file per user under the server's data folder
/// (not the plugin folder, so picks survive plugin upgrades).
/// Also holds the TMDb and Seerr keys in their own file so they never travel with the plugin's normal settings.
/// </summary>
public class PicksStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _root;
    private readonly ILogger<PicksStore> _logger;
    private readonly object _lock = new();
    private readonly PrivateJsonFile<Secrets> _secrets;

    public PicksStore(IApplicationPaths applicationPaths, ILogger<PicksStore> logger)
    {
        _root = Path.Combine(applicationPaths.DataPath, "medic-picks");
        _logger = logger;
        _secrets = new PrivateJsonFile<Secrets>(Path.Combine(_root, "secrets.json"), JsonOptions, logger, "Medic Picks");
    }

    private string UsersDir => Path.Combine(_root, "users");

    public UserPicks? Load(Guid userId)
    {
        var path = Path.Combine(UsersDir, userId.ToString("N") + ".json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lock (_lock)
            {
                return JsonSerializer.Deserialize<UserPicks>(File.ReadAllText(path), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not read picks for user {UserId}", userId);
            return null;
        }
    }

    public void Save(Guid userId, UserPicks picks)
    {
        Directory.CreateDirectory(UsersDir);
        var path = Path.Combine(UsersDir, userId.ToString("N") + ".json");
        Write(path, JsonSerializer.Serialize(picks, JsonOptions));
    }

    // Under the store's lock, as two writers of one file would share its temporary file.
    private void Write(string path, string json)
    {
        lock (_lock)
        {
            AtomicFile.Write(path, json);
        }
    }

    public int CountUsersWithPicks()
    {
        return Directory.Exists(UsersDir) ? Directory.GetFiles(UsersDir, "*.json").Length : 0;
    }

    // The keys file holds the TMDb and Seerr keys: see Medic.Shared.PrivateJsonFile for how it's kept.
    public string? GetTmdbKey() => Clean(_secrets.Read().TmdbKey);

    public void SetTmdbKey(string? key) => _secrets.Update(s => s.TmdbKey = Clean(key));

    public string? GetSeerrKey() => Clean(_secrets.Read().SeerrKey);

    public void SetSeerrKey(string? key) => _secrets.Update(s => s.SeerrKey = Clean(key));

    private static string? Clean(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    // ---------- Each person's own choices on the My picks page ----------

    private string PrefsDir => Path.Combine(_root, "preferences");

    private string PrefsPath(Guid userId) => Path.Combine(PrefsDir, userId.ToString("N") + ".json");

    public UserPreferences LoadPreferences(Guid userId)
    {
        try
        {
            return ReadPreferences(PrefsPath(userId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not read preferences for user {UserId}", userId);
            return new UserPreferences();
        }
    }

    public void SavePreferences(Guid userId, UserPreferences prefs)
    {
        Directory.CreateDirectory(PrefsDir);
        Write(PrefsPath(userId), JsonSerializer.Serialize(prefs, JsonOptions));
    }

    /// <summary>
    /// Reads, changes and saves one person's choices under the store's lock, so two changes at the same
    /// time (a Hide while a rebuild forgets what was seen, say) can't undo each other.
    /// </summary>
    public UserPreferences UpdatePreferences(Guid userId, Func<UserPreferences, UserPreferences> change)
    {
        lock (_lock)
        {
            string path = PrefsPath(userId);
            UserPreferences current;
            try
            {
                current = ReadPreferences(path);
            }
            catch (JsonException ex)
            {
                // Saving over it would lose everything in it, the titles hidden among them.
                string aside = AtomicFile.SetAside(path);
                _logger.LogWarning(ex, "Medic Picks: the preferences for user {UserId} couldn't be read, so they were kept as {Aside} and new ones started", userId, aside);
                current = new UserPreferences();
            }

            var updated = change(current).Normalise();
            SavePreferences(userId, updated);
            return updated;
        }
    }

    // One person's choices, or new ones when they have none yet. Throws when the file can't be read.
    private static UserPreferences ReadPreferences(string path) =>
        File.Exists(path)
            ? (JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path), JsonOptions) ?? new UserPreferences()).Normalise()
            : new UserPreferences();

    /// <summary>
    /// Saves picks without anything the person has hidden, as their choices stand at the moment of saving:
    /// a title hidden while these picks were being built (by the nightly run, say) would otherwise come back.
    /// </summary>
    public void SaveWithoutHidden(Guid userId, UserPicks picks)
    {
        lock (_lock)
        {
            var prefs = LoadPreferences(userId);
            picks.InLibrary.RemoveAll(p => prefs.HiddenItems.Contains(p.ItemId));
            picks.Linked.RemoveAll(p => prefs.HiddenItems.Contains(p.ItemId));
            picks.Discover.RemoveAll(p => prefs.HiddenTmdb.Contains(TmdbKey(p.MediaType, p.TmdbId)));
            Save(userId, picks);
        }
    }

    /// <summary>Takes anything the person has hidden out of their saved picks.</summary>
    public void RemoveHidden(Guid userId)
    {
        lock (_lock)
        {
            if (Load(userId) is { } picks)
            {
                SaveWithoutHidden(userId, picks);
            }
        }
    }

    /// <summary>How a Discover title appears in the hidden and seen lists: "movie:123" or "tv:123".</summary>
    public static string TmdbKey(string? mediaType, int tmdbId) =>
        (mediaType == "tv" ? "tv:" : "movie:") + tmdbId.ToString(CultureInfo.InvariantCulture);

    private sealed class Secrets
    {
        public string? TmdbKey { get; set; }

        public string? SeerrKey { get; set; }
    }
}

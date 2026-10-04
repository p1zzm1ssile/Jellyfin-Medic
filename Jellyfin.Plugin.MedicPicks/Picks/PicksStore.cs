using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Stores picks as one JSON file per user under the server's data folder
/// (not the plugin folder, so picks survive plugin upgrades).
/// Also holds the TMDb key in its own file so it never travels with the plugin's normal settings.
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

    public PicksStore(IApplicationPaths applicationPaths, ILogger<PicksStore> logger)
    {
        _root = Path.Combine(applicationPaths.DataPath, "medic-picks");
        _logger = logger;
    }

    private string UsersDir => Path.Combine(_root, "users");

    private string SecretsPath => Path.Combine(_root, "secrets.json");

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
        var tmp = path + ".tmp";
        lock (_lock)
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(picks, JsonOptions));
            File.Move(tmp, path, true);
        }
    }

    public int CountUsersWithPicks()
    {
        return Directory.Exists(UsersDir) ? Directory.GetFiles(UsersDir, "*.json").Length : 0;
    }

    public string? GetTmdbKey()
    {
        try
        {
            if (!File.Exists(SecretsPath))
            {
                return null;
            }

            var secrets = JsonSerializer.Deserialize<Secrets>(File.ReadAllText(SecretsPath), JsonOptions);
            return string.IsNullOrWhiteSpace(secrets?.TmdbKey) ? null : secrets!.TmdbKey!.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Picks: could not read the TMDb key file");
            return null;
        }
    }

    public void SetTmdbKey(string? key)
    {
        Directory.CreateDirectory(_root);
        lock (_lock)
        {
            File.WriteAllText(SecretsPath, JsonSerializer.Serialize(new Secrets { TmdbKey = key?.Trim() }, JsonOptions));
        }
    }

    private sealed class Secrets
    {
        public string? TmdbKey { get; set; }
    }
}

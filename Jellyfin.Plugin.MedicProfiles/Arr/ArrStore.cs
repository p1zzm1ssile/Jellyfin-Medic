using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicProfiles.Arr;

/// <summary>One thing an admin did from the Downloads tab, kept so there's a record of it.</summary>
public class ActionRecord
{
    public DateTime Utc { get; set; }

    public string App { get; set; } = string.Empty;

    /// <summary>remove, block, import, unblock.</summary>
    public string Action { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public string? By { get; set; }
}

/// <summary>
/// Keeps the Sonarr and Radarr API keys in their own file under the server's data folder, readable
/// only through admin-only endpoints and never sent to a browser, plus a short history of actions.
/// </summary>
public class ArrStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private const int MaxHistory = 200;

    private readonly string _root;
    private readonly ILogger<ArrStore> _logger;
    private readonly object _lock = new();

    public ArrStore(IApplicationPaths applicationPaths, ILogger<ArrStore> logger)
    {
        _root = Path.Combine(applicationPaths.DataPath, "medic-profiles");
        _logger = logger;
    }

    private string SecretsPath => Path.Combine(_root, "secrets.json");

    private string HistoryPath => Path.Combine(_root, "history.json");

    public string? GetKey(ArrApp app) => app == ArrApp.Sonarr ? ReadSecrets().SonarrKey : ReadSecrets().RadarrKey;

    public void SetKey(ArrApp app, string? key)
    {
        lock (_lock)
        {
            var secrets = ReadSecrets();
            string? clean = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            if (app == ArrApp.Sonarr)
            {
                secrets.SonarrKey = clean;
            }
            else
            {
                secrets.RadarrKey = clean;
            }

            Directory.CreateDirectory(_root);
            WriteAtomic(SecretsPath, JsonSerializer.Serialize(secrets, JsonOptions));
        }
    }

    public string? GetQbitPassword() => ReadSecrets().QbitPassword;

    public void SetQbitPassword(string? password)
    {
        lock (_lock)
        {
            var secrets = ReadSecrets();
            secrets.QbitPassword = string.IsNullOrEmpty(password) ? null : password;
            Directory.CreateDirectory(_root);
            WriteAtomic(SecretsPath, JsonSerializer.Serialize(secrets, JsonOptions));
        }
    }

    public List<ActionRecord> LoadHistory()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(HistoryPath))
                {
                    return JsonSerializer.Deserialize<List<ActionRecord>>(File.ReadAllText(HistoryPath), JsonOptions) ?? new List<ActionRecord>();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: could not read the action history");
        }

        return new List<ActionRecord>();
    }

    public void Record(ActionRecord record)
    {
        try
        {
            lock (_lock)
            {
                var history = LoadHistory();
                history.Insert(0, record);
                Directory.CreateDirectory(_root);
                WriteAtomic(HistoryPath, JsonSerializer.Serialize(history.Take(MaxHistory).ToList(), JsonOptions));
            }
        }
        catch (Exception ex)
        {
            // The history is a convenience; the action itself has already happened.
            _logger.LogWarning(ex, "Medic Profiles: could not save the action history");
        }
    }

    private Secrets ReadSecrets()
    {
        try
        {
            if (File.Exists(SecretsPath))
            {
                return JsonSerializer.Deserialize<Secrets>(File.ReadAllText(SecretsPath), JsonOptions) ?? new Secrets();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medic Profiles: could not read the keys file");
        }

        return new Secrets();
    }

    // Writes to a temporary file first, so a crash mid-write can't leave a half-written file behind.
    private void WriteAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        lock (_lock)
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
    }

    private sealed class Secrets
    {
        public string? SonarrKey { get; set; }

        public string? RadarrKey { get; set; }

        public string? QbitPassword { get; set; }
    }
}

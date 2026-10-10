using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MedicProfiles.Configuration;

/// <summary>
/// Medic Profiles' settings. The Sonarr and Radarr API keys are NOT kept here; they live in a
/// separate admin-only file (see ArrStore), so they never travel with the plugin's normal settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Sonarr's address as the Jellyfin server reaches it, e.g. http://192.168.1.10:8989.</summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>Radarr's address as the Jellyfin server reaches it, e.g. http://192.168.1.10:7878.</summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>Downloads stuck for at least this many hours are flagged on the Downloads tab.</summary>
    public int StuckAfterHours { get; set; } = 6;

    /// <summary>qBittorrent's Web UI address, for blocking unsafe file types. Its password is kept with the keys.</summary>
    public string QbitUrl { get; set; } = string.Empty;

    public string QbitUser { get; set; } = string.Empty;
}

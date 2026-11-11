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

    /// <summary>Prowlarr's address as the Jellyfin server reaches it, e.g. http://192.168.1.10:9696. Optional: only for the Indexers tab.</summary>
    public string ProwlarrUrl { get; set; } = string.Empty;

    /// <summary>Downloads stuck for at least this many hours are flagged on the Downloads tab.</summary>
    public int StuckAfterHours { get; set; } = 6;

    /// <summary>qBittorrent's Web UI address, for blocking unsafe file types. Its password is kept with the keys.</summary>
    public string QbitUrl { get; set; } = string.Empty;

    public string QbitUser { get; set; } = string.Empty;

    /// <summary>The patterns Medic Profiles added to qBittorrent's excluded file names, so unblocking removes only those.</summary>
    public string[] QbitPatternsAdded { get; set; } = System.Array.Empty<string>();

    /// <summary>Whether that list was switched on before Medic Profiles first blocked anything; null while nothing is blocked.</summary>
    public bool? QbitListWasOn { get; set; }

    /// <summary>
    /// The qBittorrent (scheme, host and port) the two above were recorded on. Empty in settings saved before it was
    /// recorded, when the record is taken as this qBittorrent's.
    /// </summary>
    public string QbitPatternsHost { get; set; } = string.Empty;

    /// <summary>"transmission" or "deluge" for the torrent guard (see Arr/TorrentGuard.cs), or empty for neither.</summary>
    public string TorrentClientKind { get; set; } = string.Empty;

    /// <summary>The Web UI's address, e.g. http://192.168.1.10:9091 for Transmission or :8112 for Deluge.</summary>
    public string TorrentClientUrl { get; set; } = string.Empty;

    /// <summary>Transmission's RPC user name. Deluge's Web UI has a password only.</summary>
    public string TorrentClientUser { get; set; } = string.Empty;

    /// <summary>Skip program files in Transmission's or Deluge's torrents.</summary>
    public bool TorrentBlock { get; set; }

    /// <summary>Skip archives (.rar, .zip, .7z) too.</summary>
    public bool TorrentArchives { get; set; }
}

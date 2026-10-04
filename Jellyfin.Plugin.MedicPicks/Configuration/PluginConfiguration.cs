using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MedicPicks.Configuration;

/// <summary>
/// Admin settings for Medic Picks.
/// The TMDb key is NOT kept here; it lives in a separate admin-only file (see PicksStore).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Build picks from titles already on the server.</summary>
    public bool EnableLibraryPicks { get; set; } = true;

    /// <summary>How many "on your server" picks each user gets.</summary>
    public int LibraryPickCount { get; set; } = 20;

    /// <summary>Write each user's library picks to a private playlist (shows in every Jellyfin app).</summary>
    public bool CreatePlaylists { get; set; } = true;

    /// <summary>Name of the private playlist created for each user.</summary>
    public string PlaylistName { get; set; } = "Picks for you";

    /// <summary>Suggest titles that aren't on the server (needs a TMDb key).</summary>
    public bool EnableDiscover { get; set; }

    /// <summary>How many "not on the server yet" picks each user gets.</summary>
    public int DiscoverPickCount { get; set; } = 20;

    /// <summary>TMDb language for titles and overviews, e.g. en-GB.</summary>
    public string TmdbLanguage { get; set; } = "en-GB";

    /// <summary>Optional Jellyseerr address. When set, Discover picks link to a request page there.</summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>Users who should not get Discover picks (e.g. children's accounts). Library picks still respect parental controls.</summary>
    public string[] DiscoverDisabledUserIds { get; set; } = Array.Empty<string>();

    /// <summary>Minimum watched titles before picks are built for a user.</summary>
    public int MinimumWatchedTitles { get; set; } = 3;
}

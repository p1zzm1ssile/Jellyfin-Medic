using MediaBrowser.Controller.Entities;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>Checks: libraries, Live TV and the network.</summary>
public sealed partial class DiagnosticsEngine
{
    // ---------- Libraries ----------

    private void CheckLibraries(List<LibraryFacts> libraries)
    {
        foreach (var lib in libraries)
        {
            foreach (var location in lib.Locations.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                if (!Directory.Exists(location))
                {
                    Add(AreaLibraries, Sev.Problem, $"{lib.Name}: a library folder can't be found", location,
                        HostPlatform.IsContainer
                            ? "Check the folder is mapped into the container and the drive is mounted"
                            : "Check the drive is connected and mounted, and that Jellyfin can read the folder",
                        "Jellyfin can't see anything in that folder, and a scan may mark its items as missing.", WhereDocker);
                }
            }

            if (lib.IsStreamed && lib.ItemCount >= 50_000)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: a very large IPTV library", $"{lib.ItemCount:N0} items",
                    "Import only the categories you actually watch",
                    "Every list, search and home screen row has to work through these items. Library size is the biggest single factor in how fast this server feels.",
                    "Your IPTV plugin's settings (for example Xtream Library's categories)");
            }

            var opts = lib.Options;
            if (opts is null)
            {
                continue;
            }

            if (lib.IsStreamed)
            {
                if (SettingsReader.Bool(opts, "EnableChapterImageExtraction") == true)
                {
                    Add(AreaLibraries, Sev.Improve, $"{lib.Name}: chapter images are on for a streamed library", "On", "Off",
                        "This library is mostly .strm links to online streams. Extracting images means opening each stream, which is slow and can trip your provider's connection limits.",
                        WhereLibraries);
                }

                if (SettingsReader.Bool(opts, "EnableTrickplayImageExtraction") == true)
                {
                    Add(AreaLibraries, Sev.Improve, $"{lib.Name}: trickplay is on for a streamed library", "On", "Off",
                        "Trickplay would have to read every online stream end to end.", WhereLibraries);
                }

                if (SettingsReader.Bool(opts, "EnableRealtimeMonitor") == true)
                {
                    Add(AreaLibraries, Sev.Tip, $"{lib.Name}: real-time monitoring is on", "On", "Off",
                        "The plugin that writes these .strm files changes many at once; a scheduled scan picks them up more efficiently than thousands of file-change events.",
                        WhereLibraries);
                }
            }
            else if (lib.ItemCount >= 5_000 && SettingsReader.Bool(opts, "EnableRealtimeMonitor") == true)
            {
                string type = (lib.CollectionType ?? string.Empty).ToLowerInvariant();
                string arr = type == "tvshows" ? "Sonarr" : type == "movies" ? "Radarr" : string.Empty;
                string unraidNote = HostPlatform.Kind == HostKind.Unraid ? ", and on Unraid user shares change events aren't always reliable" : string.Empty;
                if (arr.Length > 0)
                {
                    Add(AreaLibraries, Sev.Tip, $"{lib.Name}: real-time monitoring on a large library", "On",
                        $"Off, with {arr} set to notify Jellyfin, and a daily scan",
                        $"Watching a large library takes a system file watcher for every folder{unraidNote}. {arr}'s notifications update Jellyfin instantly and precisely.",
                        WhereLibraries + $"; {arr} → Settings → Connect → Emby / Jellyfin");
                }
                else
                {
                    Add(AreaLibraries, Sev.Tip, $"{lib.Name}: real-time monitoring on a large library", "On",
                        "Off, with a daily library scan",
                        $"Watching a large library takes a system file watcher for every folder{unraidNote}. A daily scan picks up new files reliably.",
                        WhereLibraries);
                }
            }

            if (SettingsReader.Bool(opts, "ExtractChapterImagesDuringLibraryScan") == true)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: chapter images are extracted during scans", "On", "Off",
                    "This makes every library scan much longer. The nightly \"Extract Chapter Images\" task does the same job off-peak.",
                    WhereLibraries);
            }

            if (SettingsReader.Bool(opts, "ExtractTrickplayImagesDuringLibraryScan") == true)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: trickplay images are made during scans", "On", "Off",
                    "Scans take far longer. Let the scheduled trickplay task do it overnight instead.", WhereLibraries);
            }

            long? refreshDays = SettingsReader.Number(opts, "AutomaticRefreshIntervalDays");
            if (refreshDays is > 0 and < 30 && lib.ItemCount >= 2_000)
            {
                Add(AreaLibraries, Sev.Improve, $"{lib.Name}: metadata is re-downloaded every {refreshDays} days", $"Every {refreshDays} days",
                    "Never, or every 90 days",
                    $"With {lib.ItemCount:N0} items, that's thousands of lookups a day to TMDB and others, slowing the server and risking rate limits.",
                    WhereLibraries);
            }

            // Jellyfin only measures loudness for music, and only shows this option on music libraries.
            // It's on by default everywhere else too, where it does nothing and can't be switched off.
            bool isMusic = string.Equals(lib.CollectionType, "music", StringComparison.OrdinalIgnoreCase);
            if (isMusic && SettingsReader.Bool(opts, "EnableLUFSScan") == true && lib.ItemCount >= 5_000)
            {
                Add(AreaLibraries, Sev.Tip, $"{lib.Name}: audio normalisation scanning is on", "On", "Off unless you use it",
                    "Measuring loudness reads every audio file in full, which is slow on a large library.", WhereLibraries);
            }
        }
    }

    // ---------- Live TV ----------

    private void CheckLiveTv(object? liveTv)
    {
        CheckOrphanChannels();

        long? guideDays = SettingsReader.Number(liveTv, "GuideDays");
        if (guideDays > 7)
        {
            Add(AreaLiveTv, Sev.Tip, "A long TV guide is downloaded", $"{guideDays} days", "3–4 days",
                "The guide is downloaded for every channel; with a big IPTV list, extra days make Refresh Guide much slower and the database bigger.",
                "Dashboard → Live TV → Guide data");
        }
    }

    /// <summary>
    /// Live TV channels left behind after a source or categories were removed. Jellyfin doesn't always
    /// clear these itself. Read-only: Medic never edits the database; it points at the safe fix.
    /// </summary>
    private void CheckOrphanChannels()
    {
        int channels;
        try
        {
            var query = new InternalItemsQuery { Recursive = true };
            var prop = typeof(InternalItemsQuery).GetProperty("IncludeItemTypes");
            var element = prop?.PropertyType.GetElementType();
            if (prop is null || element is null || !element.IsEnum || !Enum.IsDefined(element, "LiveTvChannel"))
            {
                return;
            }

            var kinds = Array.CreateInstance(element, 1);
            kinds.SetValue(Enum.Parse(element, "LiveTvChannel"), 0);
            prop.SetValue(query, kinds);
            channels = _library.GetCount(query);
        }
        catch
        {
            return;
        }

        // Only worth raising on a big list, where deselected categories leave thousands behind.
        if (channels >= 2000)
        {
            Add(AreaLiveTv, Sev.Tip, "A very large number of Live TV channels", $"{channels:N0} channels",
                "If you've deselected categories, clear the leftovers the safe way (below)",
                "After you deselect IPTV categories, Jellyfin can leave the old channels behind in its database. The supported ways to clear them are: let Xtream Library's own cleanup remove them on its next sync (it has a 'clean up orphans' option), or remove and re-add the Live TV source in Dashboard → Live TV, which rebuilds the channel list. Medic can clear leftover stream files on disk (see the Dashboard maintenance panel), but it never edits Jellyfin's database — a hand-written database delete risks corrupting the whole server.",
                "Dashboard → Live TV, and Xtream Library settings");
        }
    }

    // ---------- Network ----------

    private void CheckNetwork(object? network)
    {
        if (network is null)
        {
            return;
        }

        bool remote = SettingsReader.Bool(network, "EnableRemoteAccess") ?? false;
        bool https = (SettingsReader.Bool(network, "EnableHttps") ?? false) || (SettingsReader.Bool(network, "RequireHttps") ?? false);
        var proxies = SettingsReader.List(network, "KnownProxies") ?? new List<string>();

        if (remote && !https && proxies.Count == 0)
        {
            Add(AreaNetwork, Sev.Tip, "Remote access is on without HTTPS", "Remote access on, no HTTPS or known proxy",
                "If you watch away from home, use a reverse proxy with HTTPS (e.g. Nginx Proxy Manager or SWAG) and add it under Known proxies",
                "Without HTTPS, passwords and viewing activity travel unencrypted over the internet. Ignore this if Jellyfin is only reachable at home or over a VPN.",
                "Dashboard → Networking");
        }
    }
}

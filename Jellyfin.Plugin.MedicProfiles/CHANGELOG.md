# Medic Profiles changelog

All notable changes to Medic Profiles are recorded here. The newest version is at the top.
Jellyfin Medic and Medic Picks have their own changelogs.

## [1.1.2] – 2026-10-14

### Added
- **Remove can leave the download in the client.** The Remove panel has a new tick box, on by default. Untick it and the download stays in the download client, still seeding, while Sonarr or Radarr stops tracking it, as their own "Ignore download" does. Handy for a finished download that couldn't be imported.

### Fixed
- **The page says when a list can't be loaded.** The Blocked, Indexers, History and Settings tabs stayed blank or half filled when Medic Profiles didn't answer, and removing a saved key showed nothing if it failed. Each now says so. The Downloads tab's message for this no longer starts with "Radarr:".
- **The keys file is now private.** `medic-profiles/secrets.json`, which holds the Sonarr and Radarr API keys and the qBittorrent password, could be read by any account on the server. It's now only readable by the account Jellyfin runs as, and an existing file is tightened the next time it's read. Windows is unchanged, as it uses its own folder permissions.
- **Saved keys stay with their server.** Changing Sonarr's, Radarr's or qBittorrent's address kept the saved key or password, and the next test or list sent it to the new address. When the address now points at a different server, the saved key or password is cleared and has to be typed again. A keys file that can't be read is also kept as `secrets.json.bad` instead of being replaced by one holding only the newest key (the other keys used to vanish without a word).
- **Blocking files in qBittorrent keeps your own list as it was.** Block removed any of Medic Profiles' patterns you had added yourself (`*.zip`, say, with archives left unticked) while saying your entries were kept, and Unblock left the list switched on even if you had it off. Medic Profiles now remembers what it added and whether the list was on: Block only adds what's missing, and Unblock removes only what it added and switches the list back on or off as it was. The settings page now also says that qBittorrent applies the list to every torrent, not only films and series.
- **Fewer sign-ins to qBittorrent, and no ban from a wrong password.** Every time the Settings tab opened, Medic Profiles signed in to qBittorrent again and never signed out. With a wrong password saved, each visit counted as a failed sign-in, and qBittorrent blocks the server's address after a few. It now keeps one session (and signs it out when the address or login changes), tries a refused password only once every 5 minutes, and says when qBittorrent answered but sent no settings instead of suggesting an update.
- **Custom format advice no longer points at the wrong format.** Each piece of advice named the first custom format whose name matched a broad pattern, and listed every profile that didn't penalise that one format. With TRaSH's formats it could tell you to refuse "DV HDR10" (which has the fallback) or "TrueHD ATMOS" (lossless audio you had scored up), even where "DV (w/o HDR fallback)" and "DTS" were already refused, and it suggested holding back HEVC in 4K profiles too. Advice now looks at every matching format: profiles that already score one down are fine, profiles that score one up made a choice it won't overrule, and the Dolby Vision advice only matches formats for Dolby Vision without a fallback. The HEVC advice leaves out profiles that allow 2160p.
- **A long download queue says when it's cut short.** Medic Profiles reads up to 1,000 queue entries from each app and didn't say when there were more. The Downloads summary now says how many of how many are shown.

## [1.1.1] – 2026-10-11

### Added
- **Contact tab.** Report a problem on GitHub (the new issue is already titled for Medic Profiles), see the open issues, or email p1zzm1ssilerepo@gmail.com for anything private.

## [1.1.0] – 2026-10-10

### Added
- **Block unsafe files in qBittorrent.** Under Settings, add qBittorrent's Web UI address and sign-in, then press **Block unsafe files**: qBittorrent then skips program files (.exe, .bat, .scr, .msi and similar) that come inside film and series torrents, so they never reach your disk. It uses qBittorrent's own "Excluded file names" list (version 4.6 or later) and keeps anything you'd added there yourself. Archives (.rar, .zip, .7z) can be blocked too, but that's off by default, as many genuine Usenet and scene releases come packed and Sonarr and Radarr unpack them. Sonarr and Radarr can't do this themselves, as they only see release names, not the files inside. Other download clients aren't supported yet.
- **Indexers tab.** Every indexer Sonarr and Radarr search, and whether it's working: failing (since when, and when Sonarr or Radarr will try it again), working (and whether it's used for RSS, automatic or manual searches), or switched off. Indexer warnings from Sonarr's and Radarr's own health checks are shown at the top. Indexers added by Prowlarr show here too, as Prowlarr adds them to Sonarr and Radarr.

## [1.0.1] – 2026-10-06

### Fixed
- **Profiles.** The "HEVC plays fine here, which saves space" tip now only appears for profiles that actually give an x265 / HEVC custom format a negative score, and names that format. It used to list every profile, even ones already scoring x265 at 0 or above.

## [1.0.0] – 2026-10-06

### Added
- First release. Sonarr and Radarr from your Jellyfin dashboard, under **Plugins → Medic Profiles**. Admins only.
- **Downloads.** Everything Sonarr and Radarr are downloading in one list, with progress and when it should finish. A season pack is one row, not one per episode. Problems come first, with Sonarr's or Radarr's own explanation: couldn't be imported, failed, unwanted files (such as a .exe), not matched to anything, and downloads with no progress after a few hours (6 by default). Updates every 30 seconds.
- **Remove**, or **Remove and block**: takes it out of Sonarr or Radarr and the download client. Blocking stops that release ever being grabbed again and, unless you untick it, searches for a different one straight away. Every action asks first.
- **Import manually.** For downloads that finished but weren't imported: see each file, what Sonarr or Radarr thinks it is, and why it refused. Change the film, series or episodes if the match is wrong, choose move or copy, and import. Only files that are actually in that download can be imported.
- **Blocked.** The releases Sonarr and Radarr won't grab again, with Unblock.
- **Profiles.** Read-only advice on your quality profiles and custom formats:
  - From what your server plays (using Jellyfin Medic's record of transcoded files, if Medic is installed): avoid DTS- or TrueHD-only audio, HEVC or AV1 when your devices can't play them, Dolby Vision with no HDR10 fallback, 4K for devices that can't show it, and Remux when its bitrate is too high to stream.
  - From the profiles themselves: cinema recordings and unknown quality allowed, minimum custom format scores nothing can reach, custom formats that do nothing, and profiles nobody uses.
  - Each suggestion says which profiles, what to change, why, and what was seen on your server.
- **History** of what was removed, blocked, unblocked or imported from here, and by whom.
- The Sonarr and Radarr API keys are kept on the server in their own file, readable only by admins, and never sent to a browser.

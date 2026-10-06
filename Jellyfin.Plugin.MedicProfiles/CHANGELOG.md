# Medic Profiles changelog

All notable changes to Medic Profiles are recorded here. The newest version is at the top.
Jellyfin Medic and Medic Picks have their own changelogs.

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

# Changelog

All notable changes to Jellyfin Medic are recorded here. The newest version is at the top.

This project uses [semantic versioning](https://semver.org): given a version X.Y.Z,
Z changes for fixes, Y for new features, X for changes that break compatibility.

## [1.0.4] – 2026-10-03

### Added
- **Track cleanup (Tracks tab).** Removes unwanted audio and subtitle tracks from local
  files by remuxing (no re-encode, no quality loss). Keeps your languages, forced
  subtitles, and the native track where it's the only one, so anime and foreign films
  stay watchable. Scans and previews first, keeps the original beside a stripped copy by
  default, groups undetermined tracks for review, and has concurrency and thread options.
  Skips IPTV.

### Fixed
- The Tracks endpoints are now correctly registered, so the scan works.

## [1.0.3] – 2026-10-03

### Added
- **Plugin directory.** A new tab lists community Jellyfin plugins — what they do and
  where to find them — fetched live from the awesome-jellyfin project and shown with
  thanks to them.
- **"What's new" panel.** After updating, Medic shows a short summary of what changed in
  the new version, once.
- **Scheduling help modes.** A new setting: "Off" (you run Preview/Apply yourself)
  or "Suggest" (Medic flags a clearly better schedule in Checks and waits for you to
  Apply). Suggest is the default. Medic always backs up and logs before any change.
- **Help on every setting.** A "?" button beside each setting explains what it does
  and why.
- **Memory load guard.** If memory climbs past a ceiling (85% by default) while more
  than one heavy task is running, Medic stops the extra heavy tasks to keep the server
  up, then restarts them one at a time once memory recovers. On/off and the ceiling are
  in Settings. The Dashboard shows memory as a percentage and reports any guard action.
- **Safe IPTV clean-up.** Finds and removes leftover IPTV stream (.strm) files for
  deselected categories, and flags a very large Live TV channel count with the supported
  ways to clear orphaned channels. Medic never edits Jellyfin's database.

## [1.0.2] – 2026-10-02

### Changed
- The exposure check now takes a domain or IP, port and protocol (pre-filled with what
  Medic found), with a shortcut to test the raw 8096 port.
- A plugin repository is only reported as not responding after it fails twice in a row,
  so a momentary blip is no longer called "dead".
- Long error messages on finding cards can now be expanded and scrolled in full.
- Clearer wording when a plugin simply stores no settings to check.

## [1.0.1] – 2026-10-02

### Fixed
- The exposure check could wrongly report a server as unreachable; it now finds the
  public IP and tests reliably.
- Blank connection settings are only flagged when a plugin has none filled in, so
  optional features (such as Xtream Library's Dispatcharr address) aren't reported.
- Works with the way Jellyfin 12.1 provides the user list, fixing a build error.

## [1.0.0] – 2026-10-02

First release. Jellyfin Medic merges the earlier Task Advisor and Setup Optimiser
plugins into one, and adds a new dashboard, a timeline schedule and several new checks.

### Added
- **Dashboard** with health, today's tasks, live server status, top issues, last 24
  hours, users, updates and a maintenance panel.
- **Schedule** as a week timeline that places maintenance tasks in your quiet hours,
  learned from your own viewing pattern, with preview, apply, backup and restore.
- **Checks** covering hardware and transcoding, storage and disk health, server and
  library settings, Live TV, network, scheduled tasks, users and security, plugins and
  the log, each with an Ignore option.
- **Tests**: playback (GPU transcoding), image extraction (GPU vs CPU) and server
  internet speed.
- **IPTV** analysis: genre and country breakdowns, how much gets watched, and duplicate
  channels.
- **Users and security**: inactive and unprotected accounts, failed-login bursts,
  logins from several places, and over-broad library access.
- **Settings for support**: a masked copy of plugin settings, with an optional anonymous
  system summary.
- On first start, migrates data from Task Advisor and Setup Optimiser.

[1.0.4]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.4
[1.0.3]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.3
[1.0.2]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.2
[1.0.1]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.1
[1.0.0]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.0

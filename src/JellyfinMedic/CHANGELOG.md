# Changelog

All notable changes to Jellyfin Medic are recorded here. The newest version is at the top.

This project uses [semantic versioning](https://semver.org): given a version X.Y.Z,
Z changes for fixes, Y for new features, X for changes that break compatibility.

## [1.0.9] – 2026-10-04

### Added
- **Restarts for updates, handled for you.** A new scheduled task, "Restart Jellyfin for waiting updates", restarts Jellyfin when plugin updates are waiting, but only when nobody's watching, no track cleanup is running and no other scheduled task is busy. If it isn't safe, it checks again every 5 minutes for up to an hour, then waits for its next run. It runs at 04:00 by default; change the time under Scheduled Tasks. Checks also tells you when a restart is waiting.
- **Scheduled restart.** A second task, "Scheduled restart", restarts Jellyfin at whatever times you give it, for example once a week to clear memory, with the same safety checks. It has no time set to begin with, so it never restarts your server until you add one.
- **Pause, resume and a time window for track cleanup.** Pause finishes the file it's on, then waits until you press Resume. You can also limit track cleanup to set hours, such as 01:00 to 07:00: start a run at any time and it waits for the window, works through it, and carries on in the next one. By default it also holds off while anyone is watching. Time spent waiting is left out of the time-left estimate.
- **The issue, on the page where you fix it.** When you follow a "Where" link, the Jellyfin page it opens shows the issue in a small box: what's set now, what Medic suggests, and why. You can drag it anywhere, minimise it or close it. It's kept in your browser only, and disappears when you close it or after 30 minutes.

### Fixed
- Links for plugin issues now open that plugin's own settings page, instead of the list of all plugins.

## [1.0.8] – 2026-10-04

### Changed
- **One file per title.** Track cleanup no longer leaves a stripped copy beside the original. The cleaned file takes the original's place under the same name, so Jellyfin keeps the same item, artwork and watched status. The original is kept in a hidden .medic-originals folder next to it, which Jellyfin, Sonarr and Radarr ignore, until you choose "Delete the kept originals".

### Added
- **Remove duplicate files.** A button on the Tracks tab puts every title left with several copies by earlier versions back to a single file under its original name, and deletes the copies of copies. Tick "Delete the originals too" to keep only the cleaned file and free the most space.
- **Kept originals at a glance.** The Tracks tab shows how many originals Medic is keeping and how much space they take, with a button to delete them once you're happy.

### Fixed
- Track cleanup could pick up its own stripped copies and strip them again, leaving files like "Film.medic-stripped.medic-stripped.mkv". It now never touches its own files.

## [1.0.7] – 2026-10-04

### Fixed
- **Track scans carry on when you leave the tab.** Scan and preview now runs on the server, with a "Scanning x of y files" progress line. Switching tabs, or leaving Medic and coming back, shows the scan's live progress or its last result, so there's no need to scan again. The last result is cleared when you change track settings or strip tracks, because it would be out of date.

## [1.0.6] – 2026-10-04

### Added
- **Media report.** A new read-only tab shows what formats your files are in, roughly how much space converting them to HEVC could save (with a GPU encoder and with software encoding), and which files are likely to make Jellyfin transcode, such as DTS-only audio or picture-based subtitles. It reads what Jellyfin already knows about each file, so nothing is rescanned and nothing is changed.
- **Files that get transcoded.** Medic's 5-minute playback check now also notes which files were being transcoded and why, and the Media report lists them. It stores titles and reasons only, not who was watching.
- **Links to the fix.** The "Where" tips on Checks and on plugin suggestions are now links. Jellyfin settings pages open in a new tab, and links to Medic's own tabs open in place.
- **Advice for your platform.** Medic now works out where Jellyfin is running (Unraid, TrueNAS SCALE, Proxmox, Docker, Linux, Windows or macOS) and words its advice and "Where" directions for that setup, from passing the GPU through to moving Jellyfin's data. Unraid-only advice now only appears on Unraid, and the GPU device checks, which can only see devices on Linux, no longer misfire on Windows or macOS.
- **Time left for track cleanup.** While tracks are being removed, the Tracks tab shows how much of the data is done, roughly how long is left, and about when it will finish. It's worked out from file sizes, so a large 4K file counts for more than a small DVD rip.

### Changed
- **Track settings are now on the Tracks tab**, next to the tool they control, with their own Save button.

### Fixed
- TMDb, OMDb, Fanart, TheTVDB and similar plugins are no longer reported as "not set up" when their API key is blank. They come with a built-in key, so blank is normal.
- The "downloads full-size original images" tip for TMDb now counts only films and TV, and only appears for large film and TV libraries. Music was being counted before.
- Real-time monitoring advice no longer suggests Sonarr or Radarr for music libraries. It names Sonarr for TV, Radarr for films, and a daily scan for everything else.
- Plugin findings now name the plugin in "Where", instead of "(plugin)".

## [1.0.5] – 2026-10-04

### Added
- **Plugin suggestions.** The Plugin directory tab now opens with plugins suggested for your server, based on what's in your libraries, what your users actually watch and the plugins you already have.
- Suggests plugins worth adding, such as Intro Skipper when most viewing is TV, an anime metadata provider when anime is found, or lyrics for a music library.
- Flags plugins you probably don't need, such as an anime provider with no anime on the server, or Bookshelf, which Jellyfin 12 has replaced.
- Each suggestion explains why, says how important it is, and shows whether it's in your plugin catalogue. Nothing is installed or removed for you.
- **Untagged subtitles.** A new Track cleanup setting removes subtitle tracks with no language tag, often a whole disc's worth of foreign subtitles, while always keeping untagged audio, which is often a film's main soundtrack.
- It keeps the first untagged subtitle in films that have no subtitle in your languages, since that's usually the film's own language. An optional setting also removes a film's only subtitle when it's untagged.
- Films whose audio is tagged as a language you don't keep always keep their subtitles, so foreign films and anime stay watchable.

### Changed
- The old "allow removing undetermined tracks" setting is now labelled "Remove all untagged tracks, audio included", to make clear it can remove a film's soundtrack.

### Fixed
- The Undetermined tracks list counted a file once for every untagged track in it, so the file counts were too high and the same file repeated in the examples. Files and tracks are now counted separately.

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

[1.0.9]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.9
[1.0.8]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.8
[1.0.7]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.7
[1.0.6]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.6
[1.0.5]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.5
[1.0.4]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.4
[1.0.3]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.3
[1.0.2]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.2
[1.0.1]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.1
[1.0.0]: https://github.com/p1zzm1ssile/Jellyfin-Medic/releases/tag/v1.0.0

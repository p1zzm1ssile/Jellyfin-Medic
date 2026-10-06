# Medic Picks changelog

All notable changes to Medic Picks are recorded here. The newest version is at the top.
Jellyfin Medic has its own changelog in the main CHANGELOG.md.

## [1.1.0] – 2026-10-06

### Added
- **Choose what you see.** Everyone's My picks page now has a settings box:
  - **Show:** films and series, films only, or series only.
  - **Genres:** tick as many as you like. Every standard genre is offered (Action, Adventure, Animation, Anime, Biography, Comedy, Crime, Documentary, Drama, Family, Fantasy, History, Horror, Kids, Music, Mystery, Romance, Science Fiction, Sport, Thriller, War, Western), plus any others in your library. Titles matching any ticked genre are shown. Paired TV genres count too, so Science Fiction also finds "Sci-Fi & Fantasy" series.
  - **Seasonal:** Christmas and Halloween, found from tags, genres and titles.
  - **How many:** 5, 10, 15, 20, 25 or 30 picks in each section.
  - **Update my picks** rebuilds your picks straight away with your choices.
- **Linked to what you've watched.** A new section with titles on the server from the same world as something you've watched: a series' films and the other way round (The Seven Deadly Sins series and its films), sequels (Frozen, Frozen II), the rest of a collection, and titles sharing a franchise tag, such as Marvel series and the MCU films. Each says why it's there. Admins can turn it off in the settings.
- **Ignore – don't recommend again.** On any pick, removes it straight away and it's never suggested again. "Show them again" brings ignored titles back.

### Changed
- **"Dubbed audio only" replaces "Anime: English dubs only"** and now covers every title, not just anime. On your server, only titles with audio in your language (set by the TMDb language) are shown. Suggestions not on the server that were first made in another language are marked "audio not confirmed". If you had the old option ticked, the new one is ticked for you.

## [1.0.4] – 2026-10-06

### Fixed
- Duplicate "Picks for you" playlists could pile up if a rebuild was stopped partway, or if two rebuilds ran at once. Picks now saves the new playlist straight away and builds one person at a time.
- Turning Discover off for someone now clears their old Discover picks.
- Saving your preferences repeatedly no longer starts several rebuilds at once.
- Preferences and keys are saved safely, so a crash mid-save can't wipe them.

## [1.0.3] – 2026-10-05

### Fixed
- Seerr and preference handling made more reliable.

## [1.0.2] – 2026-10-05

### Added
- One-tap Seerr requests, made under each person's own Seerr account.
- English dubs for anime.

## [1.0.1] – 2026-10-04

### Added
- A My picks link in everyone's Jellyfin menu, added automatically.

## [1.0.0] – 2026-10-04

### Added
- First release: a personal "Picks for you" playlist in every app, and a My picks page.

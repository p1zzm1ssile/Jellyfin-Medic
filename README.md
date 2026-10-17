![Jellyfin Medic](src/JellyfinMedic/thumb.png)

# Jellyfin Medic

Health checks, performance tests and usage-aware task scheduling for your Jellyfin server.

[![Build and Release](https://github.com/P1zzm1ssile/Jellyfin-Medic/actions/workflows/release.yml/badge.svg)](https://github.com/P1zzm1ssile/Jellyfin-Medic/actions/workflows/release.yml) [![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE) ![Jellyfin 12.1+](https://img.shields.io/badge/Jellyfin-12.1%2B-00a4dc.svg)

This repository has four plugins: **Jellyfin Medic**, for the person who runs the server; **[Medic Picks](#medic-picks)**, personal viewing suggestions for everyone who uses it; **[Medic Profiles](#medic-profiles)**, Sonarr and Radarr from your Jellyfin dashboard; and **[Medic Posters](#medic-posters)**, an animated poster for every film.

---

## What it does

Jellyfin Medic looks after a busy Jellyfin server. It checks your settings, hardware, storage, libraries, schedules, plugins and users, tells you what to change and where, and schedules your maintenance tasks to run when nobody's watching. It's built for large libraries, IPTV and setups with lots of community plugins.

Everything lives on one page in the dashboard sidebar, under **Plugins → Jellyfin Medic**. Only administrators can use it.

### Dashboard

A single screen of 15 tiles: health (how many things need fixing), what the server is doing right now (CPU, memory, who's watching, running tasks), disks, this week (issues fixed, space freed), top issues, load over the last 10 minutes (with any tasks LoadGuard held back), today's tasks as planned against what actually ran, busy hours and the tasks that run in them, why things transcode, each disk's trend and forecast, the network (speed test and streaming limit), repeated log errors, people and access, updates waiting, and a maintenance panel to free up disk space (including old log files), check whether your server is exposed to the internet, or stop running tasks.

### Schedule

A week-at-a-glance timeline. Each task is a coloured block on its day, with shading behind it showing how busy that hour usually is and a red line marking "now". Click any task for its details. Medic works out the quietest times from your own viewing pattern and, on Linux, how busy the whole server usually is, and can run a task at different times on different days; **Preview** shows the plan before you **Apply** it, with a choice for each task (let Medic schedule it, keep its schedule, or leave it unscheduled; tasks with no schedule are never given one unless you say so), and every change is backed up so it can be undone. Jellyfin has no monthly trigger, so Medic runs monthly tasks itself. A **Restart Jellyfin for waiting updates** task finishes installing plugin updates for you, and a **Scheduled restart** task restarts Jellyfin regularly. Neither has a time set to begin with: give them one under Scheduled Tasks to turn them on. Every task with no time set is listed below the timeline with what it does and whether Medic recommends turning it on. Both only restart when nobody's watching and nothing else is running.

### Checks

Every finding, most serious first, each with its current value, the suggested value, why it matters and a link that opens the right Jellyfin page in a new tab (for plugin issues, that plugin's own settings), with directions worded for your platform. The page you land on shows the issue in a small box you can move, so it's in front of you while you fix it. Checked something and happy with it? **Ignore** it and it drops out of the counts. Findings come back on their own if the situation changes.

Medic checks, among other things:

- **Hardware and transcoding:** whether your GPU is being used, and set up to match; hardware encoding, decoding and HDR tone mapping; where transcodes are written.
- **Storage:** free space, log and database sizes, debug logging, and on Unraid, user-share paths that slow the database and disk health from Unraid's own SMART data.
- **Server and libraries:** remote streaming limits, parallel-task limits above your CPU, IPTV libraries with image extraction turned on, metadata refresh frequency, and more.
- **Scheduled tasks:** tasks running when people watch, scans that run too often, database optimisation that never runs, and failed tasks named with the plugin they came from.
- **Users and security:** accounts with no password, inactive accounts, too many admins, bursts of failed sign-ins, accounts signing in from several places, and accounts that can see every library.
- **Plugins:** broken, disabled or incompatible plugins, leftover settings files, dead plugin repositories, broken add-on scripts, and updates available.

### Tests

- **Playback test:** plays a 1080p and a 4K HDR film through Jellyfin's real transcoder and reports the speed and whether the GPU did the work.
- **Image extraction test:** times your GPU against your CPU at the work trickplay and chapter images do.
- **Internet speed test:** measures your server's own connection and recommends your remote streaming limit for however many people watch away from home.

### IPTV

Counts your IPTV films and series by genre and country, shows how much of each actually gets watched so you can trim what nobody uses, and finds live channels that appear several times in different qualities.

### Tracks

Removes unwanted audio and subtitle tracks from your local files by remuxing, so there's no re-encode and no quality loss. It keeps your languages and forced subtitles, never removes a file's last audio track, and keeps subtitles in films whose audio isn't in your languages. Tracks with no language set are listed film by film, a page at a time: you can clear untagged subtitles while keeping untagged audio, which is often a film's main soundtrack, or set a track's language yourself, one track at a time or lots at once. Tracks inside a film are labelled on the next run, and separate subtitle files are renamed so Jellyfin recognises them. Its settings sit at the top of the tab, next to the tool. **Scan and preview** shows what every file would keep before anything changes. The cleaned file keeps the original's name, so your library still has one file per title, and by default the original is kept in a hidden .medic-originals folder next to it until you delete it. A **Remove duplicate files** button tidies up titles left with extra copies by older versions. While it runs, it shows how much is done and roughly when it will finish, and you can pause it, limit it to set hours, and have it hold off while anyone is watching. IPTV is skipped, and you can list folders, shows or films for it to leave alone.

### Media report

A read-only look at your files: what formats they're in, roughly how much space converting them to HEVC could save (with a GPU encoder and with software encoding), the files that would save the most, and what's likely to make Jellyfin transcode, such as DTS-only audio or picture-based subtitles. It also lists the files Medic has caught being transcoded during playback, and why. It reads what Jellyfin already knows about each file, so nothing is rescanned and nothing is changed.

### Plugin directory

Browse community plugins from the awesome-jellyfin list, with **suggestions for your server** at the top: plugins worth adding based on what's in your libraries and what actually gets watched, and plugins you probably don't need any more. Each suggestion says why, and whether it's in your plugin catalogue. Nothing is installed or removed for you.

### Settings and support

Change Medic's own options, browse every Jellyfin setting with a red, yellow or green marker and a link to its page (Dismiss turns a marker green), and build a masked copy of your plugin settings to attach when asking for help.

---

## Requirements

- **Jellyfin 12.1 or later** for Jellyfin Medic, **12.0 or later** for Medic Picks, Medic Profiles and Medic Posters.
- **Any server language.** Jellyfin's own tasks are recognised by their keys whatever language the server uses; tasks added by plugins are recognised by their names, which plugins rarely translate.
- Runs wherever Jellyfin does: **Unraid, TrueNAS SCALE, Proxmox, Docker, Kubernetes, Linux, Windows and macOS**. Medic works out which one it's on and words its advice and directions to match. A few checks depend on the platform: GPU device checks need Linux (containers included), and disk health uses Unraid's own disk data.
- Developed and tested on **Unraid**, with both the **binhex-Jellyfin** and **linuxserver.io Jellyfin** containers. Reports from other platforms are very welcome. GPU checks cover NVIDIA, Intel and AMD.

## Installation

### From the plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and click **+**.
2. Enter:
   - **Name:** `Jellyfin Medic`
   - **URL:** `https://raw.githubusercontent.com/P1zzm1ssile/Jellyfin-Medic/main/manifest.json`
3. Save, open the **Catalog** tab, install **Jellyfin Medic**, **Medic Picks**, **Medic Profiles** and/or **Medic Posters**, and restart Jellyfin.

### Manual install

Download the latest zip from [Releases](https://github.com/P1zzm1ssile/Jellyfin-Medic/releases), create a folder in your Jellyfin plugins directory, put the DLL inside it, and restart Jellyfin:

- Jellyfin Medic: a `JellyfinMedic` folder containing `JellyfinMedic.dll`
- Medic Picks: a `MedicPicks` folder containing `Jellyfin.Plugin.MedicPicks.dll`
- Medic Profiles: a `MedicProfiles` folder containing `Jellyfin.Plugin.MedicProfiles.dll`
- Medic Posters: a `MedicPosters` folder containing `Jellyfin.Plugin.MedicPosters.dll`

Plugin images only appear when a plugin is installed from the repository.

### Moving from Task Advisor or Setup Optimiser

Medic replaces both. On first start it copies their data across (schedule backups, run history, monthly tasks, viewing pattern, ignored findings), then flags the old plugins so you can uninstall them. Nothing is lost.

---

## Medic Picks

Personal picks for everyone on your server, built from what each person actually watches.

- **A private "Picks for you" playlist** for every user, in every Jellyfin app, TV apps included. It holds titles already on your server, and series start at their first episode so people can press play straight away.
- **A "My picks" page**, linked from everyone's Jellyfin side menu automatically, showing why each title was picked, plus titles that aren't on your server yet. It works in any browser, Jellyfin Desktop and the Android and iOS apps, with no files to edit. If you use Seerr (formerly Overseerr and Jellyseerr), each suggestion gets a **Request** button that sends the request straight to Seerr under that person's own Seerr account, so their permissions, request limits and auto-approval all apply.
- **Each person chooses what they see.** On their My picks page, anyone can choose films, series or both, pick genres (including Anime, Biography, Kids and seasonal Christmas and Halloween picks), and show 5 to 30 picks (in steps of 5). "Dubbed audio only" leaves out titles on your server with no audio in your language (set by the TMDb language), and marks suggestions from outside your library as "audio not confirmed", because TMDb can't say whether a dub exists.
- **Linked to what you've watched.** A separate section with titles from the same world as something you watched: a series' films and the other way round (The Seven Deadly Sins series and its films), the rest of a collection, and titles sharing a franchise tag, such as Marvel series and the MCU films. Admins can turn it off.
- **My requests.** Everyone sees what they've asked for in Seerr and where it's got to: waiting for approval, looking for a download, downloading (how far, and roughly when it'll be ready), or ready to watch with a Play button. Progress comes from Sonarr and Radarr through Seerr, so they need to be connected in Seerr. Each person only sees their own; admins also see everyone's requests waiting for approval, and can approve or decline them on the same page.
- **Ignore.** "Ignore – don't recommend again" on any pick removes it straight away, and it's never suggested again. "Show them again" brings ignored titles back.

Choices are made in the settings box at the top of the My picks page, and **Update my picks** rebuilds that person's picks straight away. **Show me different ones** swaps them for the next best, so people can cycle through suggestions. What's new in each version is in [Medic Picks' changelog](Jellyfin.Plugin.MedicPicks/CHANGELOG.md).

Picks are rebuilt every night by the **Build personal picks** scheduled task. Picks from your library follow each user's library access and parental controls.

### Setting it up

1. Install **Medic Picks** from the catalogue and restart Jellyfin.
2. Optional: for titles that aren't on your server yet, add a free TMDb API key in Medic Picks' settings, press **Save key**, and tick **Suggest titles that aren't on the server**.
3. Optional: add your Seerr address (one your users can reach) and your Seerr API key, then press **Test Seerr**. Each person needs to have signed in to Seerr once with their Jellyfin login before their requests can go through.
4. Untick children's accounts under **Who gets Discover picks**. TMDb suggestions can't be filtered by parental controls, so accounts with any (a maximum rating, or blocked or allowed tags) start unticked and only get Discover once you tick them.
5. Press **Build picks now**, or wait for the nightly run.
6. Optional: the **My picks** menu link is on by default. You can rename it or turn it off in the same settings.

Each person needs a few watched titles before their picks appear.

### Privacy

- Picks are built and stored on your server. Each user only sees their own, and their choices (genres, ignored titles and so on) are stored on the server with them.
- Suggestions from outside your library are off by default. When an admin turns them on, the server sends TMDb the TMDb IDs of titles people have watched. No names or account details are sent.
- Posters for suggestions from outside your library and for My requests are loaded by each person's browser straight from TMDb's image server (image.tmdb.org), so TMDb sees that person's IP address and which posters they load.
- My requests is read from Seerr on the server, asking only for the signed-in person's own Seerr account, so nobody sees anyone else's requests. Only admins see everyone's requests waiting for approval, and who asked.
- The TMDb and Seerr keys are stored separately from the plugin's other settings, only admins can read them, and they never reach anyone's browser. Requests are made on the server, as the person who pressed Request.

---

## Medic Profiles

Sonarr and Radarr, from your Jellyfin dashboard, under **Plugins → Medic Profiles**. Admins only. Nothing changes in Sonarr or Radarr until you press a button.

- **Downloads.** Everything Sonarr and Radarr are downloading, in one list, with progress and when it should finish. Problems come first, with Sonarr's or Radarr's own explanation: couldn't be imported, failed, unwanted files, not matched, or stalled. **Remove**, or **Remove and block** so that release is never grabbed again and a different one is searched for.
- **Import manually.** For downloads that finished but weren't imported: see each file, what Sonarr or Radarr thinks it is and why it refused, fix the film, series or episodes if the match is wrong, and import.
- **Blocked.** What Sonarr and Radarr won't grab again, with Unblock.
- **Block unsafe files.** Tell qBittorrent to skip program files (.exe and similar) inside film and series torrents, with archives optional.
- **Indexers.** Each indexer Sonarr and Radarr search and whether it's enabled, paused after failures or switched off, with their own indexer warnings.
- **Profiles.** Advice on your quality profiles and custom formats, from what your server actually plays (with Jellyfin Medic installed, from the files it caught being transcoded and why) and from the profiles themselves. For example: avoid DTS-only audio if your TVs can't play it, avoid 4K for devices that can't show it, or untick cinema recordings. Read-only for now: you make the changes in Sonarr or Radarr.
- **History** of what was removed, blocked, unblocked or imported, and by whom.

### Setting it up

1. Install **Medic Profiles** from the catalogue and restart Jellyfin.
2. Open **Plugins → Medic Profiles → Settings**, add your Sonarr and Radarr addresses (ones the Jellyfin server can reach) and their API keys (in each: Settings → General → API Key), and press **Test**.

The keys are kept on the server in their own file, only admins can use them, and they never reach a browser. What's new in each version is in [Medic Profiles' changelog](Jellyfin.Plugin.MedicProfiles/CHANGELOG.md).

---

## Medic Posters

An animated poster for every film and series, and an animated cover for every library, in the style of the [Library Cover Maker](https://github.com/p1zzm1ssile/Library-Cover-Maker)'s library covers. Set up under **Plugins → Medic Posters** (admins only); everyone sees the posters.

- **Made from the video itself.** Each poster cuts between a few moments of the film, spread from 15% to 75% of the way through so it skips the opening titles and the credits (for a series, a moment from each of a few episodes), with the title in capitals and a rounded white frame. Choose from ten fonts, the text's size and position (top, middle or bottom), its shadow and the dark band behind it, and fades between moments or at the end. HDR films are converted to normal colour first. It uses Jellyfin's own FFmpeg, so there's nothing else to install.
- **They stay animated.** Jellyfin resizes posters before sending them to an app, which turns an animated WebP into a still picture, but it never resizes a GIF. So the posters are small GIFs, and they move in the Jellyfin website, Jellyfin Desktop and the apps that can play a GIF poster. Apps that can't show the first frame.
- **Library covers too.** Each library with videos in it (films, TV, home videos, music videos) gets a wide cover, the shape of Jellyfin's library cards, cut from moments of a few random things in it with the library's name on it. It's a GIF as well, so it moves on the home screen. Covers have their own settings, separate from the posters'.
- **Your own posters are kept.** Nothing in your media folders is changed or deleted: the GIFs live in the server's data folder, and each film's own poster is remembered. **Put back** under any poster, or **Put back the original posters**, brings them back.
- **New films get one each night** from the **Make animated posters** scheduled task. A poster you change yourself, or that a metadata refresh replaces, is left alone. **Make all posters again** and **Make all covers again** redo them all.
- **A preview** shows a poster or a cover with the settings on screen, saved or not, and its size, without changing anything. Pick the film, series or library, or let it choose one. **Use this one** puts that preview on it, so you can do them one at a time.
- **Posters and covers separately.** **Make posters now** and **Make library covers now** each make only their own kind, and each says how many there are to make and roughly how much space they'll take, worked out from the settings on screen and the ones made so far.
- **Leave libraries out**, such as IPTV ones, by unticking them. Libraries you add later are included.
- **Your own text and moments.** Give any library's cover or title's poster its own text, and pick the films and the exact moments it's cut from, with a still of each frame. A library of streams, such as IPTV, can take its cover from another library's films and keep its own name.

### Size

Because Jellyfin sends GIF posters at their full size, every poster on a page is downloaded whole. The default, 240 × 360 with three one-second moments at 8 frames a second and 96 colours, comes to anything from under 100 KB for a plain picture to about 750 KB for a busy one, such as a field of grass. The settings page has smaller sizes, fewer moments and colours, and the preview shows what each choice comes to. What's new in each version is in [Medic Posters' changelog](Jellyfin.Plugin.MedicPosters/CHANGELOG.md).

### Setting it up

1. Install **Medic Posters** from the catalogue and restart Jellyfin.
2. Open **Plugins → Medic Posters**, untick any library to leave alone, and press **Preview a poster** or **Preview a library cover** to try the settings.
3. Press **Make posters now**, or wait for the nightly run.

---

## Privacy

- **Admin only.** Every part of Jellyfin Medic requires an administrator account.
- **Your data stays on your server.** IP addresses and settings are read, shown to you, and never saved into Medic's own files.
- **Playback records are anonymous.** To learn your quiet hours and spot transcodes, Medic notes how many people are watching and which titles were transcoded and why. It doesn't record who was watching.
- **Server load is totals only.** On Linux, Medic notes how busy the whole machine is every 5 minutes (CPU, memory and disk, from the totals in /proc) and keeps them as averages by hour of the week. It never looks at which program, container or app is using them.
- **No passwords are ever read, stored or sent.** Medic deliberately does not test password strength, because that can't be done without handling real passwords.
- **What reaches the internet, and when:**
  - Each time Medic's page builds its checks (when you open it, and after **Run checks**), it checks that the addresses your server already uses still answer: each enabled plugin repository, the outside `.js` and `.css` files that JavaScript Injector scripts load, and the `@import`s in your theme's custom CSS. The results are kept for 10 minutes.
  - Your plugin repositories are also contacted through Jellyfin's own plugin catalogue: the checks and the Dashboard's Updates tile ask Jellyfin which plugin updates are waiting, and opening the Plugin directory tab (or its "Check again") asks which plugins your catalogue offers. Each answer is kept for an hour.
  - The speed test (Cloudflare) and the "Is your server open to the internet?" check run only when you press their buttons. That check connects to the address you give; for an IP address it may ask portchecker.io instead, which gets only that address and port.
  - "Use my IP + 8096" asks a public IP service for your address: ipify, or if it doesn't answer, icanhazip.com and then checkip.amazonaws.com. The answer is kept for an hour.
  - Opening the Plugin directory tab fetches the public awesome-jellyfin plugin list.
  - None of these send your settings, files or accounts.
  - Medic Picks contacts TMDb and Seerr only when an admin has set them up, and each person's browser loads its outside-library posters from image.tmdb.org (see its own privacy notes). Medic Profiles contacts only the Sonarr, Radarr and qBittorrent addresses you give it.
- **The "help improve Medic" export** masks passwords, keys, tokens, usernames and credentials in links before you ever see the file, and asks you to read it before sending. Sending it is always your choice. If anything personal reaches the maintainer, it's deleted and not used.

---

## Building and releasing

Requires the .NET 10 SDK.

```
dotnet publish src/JellyfinMedic/JellyfinMedic.csproj -c Release -o publish/JellyfinMedic
dotnet publish Jellyfin.Plugin.MedicPicks/Jellyfin.Plugin.MedicPicks.csproj -c Release -o publish/MedicPicks
dotnet publish Jellyfin.Plugin.MedicProfiles/Jellyfin.Plugin.MedicProfiles.csproj -c Release -o publish/MedicProfiles
dotnet publish Jellyfin.Plugin.MedicPosters/Jellyfin.Plugin.MedicPosters.csproj -c Release -o publish/MedicPosters
```

Medic Picks, Medic Profiles and Medic Posters can also be built for Jellyfin 10.11, where they target .NET 9 (the .NET 10 SDK builds that too). Releases are only built for Jellyfin 12, so a 10.11 server needs one built by hand:

```
dotnet publish Jellyfin.Plugin.MedicPicks/Jellyfin.Plugin.MedicPicks.csproj -c Release -o publish/MedicPicks-10.11 -p:JellyfinTarget=10.11
dotnet publish Jellyfin.Plugin.MedicProfiles/Jellyfin.Plugin.MedicProfiles.csproj -c Release -o publish/MedicProfiles-10.11 -p:JellyfinTarget=10.11
dotnet publish Jellyfin.Plugin.MedicPosters/Jellyfin.Plugin.MedicPosters.csproj -c Release -o publish/MedicPosters-10.11 -p:JellyfinTarget=10.11
```

The end-to-end tests start a real Jellyfin in Docker with the four plugins and click through their pages: `tests/e2e/run.sh` (needs Docker, ffmpeg, and Node with Playwright; see the top of the script).

Releases are automatic: push a tag like `v1.0.6` for Jellyfin Medic, `picks-v1.0.1` for Medic Picks, `profiles-v1.0.0` for Medic Profiles, or `film-posters-v1.0.0` for Medic Posters, and GitHub Actions builds the plugin, creates the release, and adds it to `manifest.json`.

---

## Free forever

Every feature in Jellyfin Medic is free and always will be. There are no paid tiers, no Pro account, no subscriptions and no telemetry, and new features will be free too. Services Jellyfin Medic works with, like TMDB, Cloudflare and the plugin repositories it checks, have their own terms.

## About this project

I'm an ISO 27001 Lead Auditor, so inspecting logs, finding bottlenecks and reviewing where processes fail is what I do every day. I'm not a professional software developer.

I run a busy Jellyfin server at home: a large library, IPTV, and dozens of community plugins. Keeping it healthy meant constant digging, heavy maintenance tasks colliding overnight and causing buffering, runaway plugins pinning the CPU, settings that quietly held the server back, and no easy way to see any of it. Stock Jellyfin gives you very little visibility into how long tasks take or what's dragging things down.

So I built the tools I wanted: first Task Advisor to schedule tasks sensibly, then Setup Optimiser to check the server's health, and finally I merged them into Jellyfin Medic. It brings the same mindset I use in auditing, find the problem, explain it plainly, point at the fix, to a Jellyfin server.

I have combined-type ADHD, dyslexia and dyscalculia. I can read code and follow its logic and security well, but writing it out myself isn't possible. So I design what Medic does and how it works, and use AI as an assistive tool to write the code from that design. I test every change against my own server and its real problems before it's released. I'm sharing it for anyone running something similar.

[Issues](https://github.com/P1zzm1ssile/Jellyfin-Medic/issues), suggestions and pull requests are welcome. Translations especially: Medic keeps all its wording in one place so other languages can be added, and I'd rather have them checked by people who speak the language than machine-translated.

For anything that doesn't belong on a public page, email [p1zzm1ssilerepo@gmail.com](mailto:p1zzm1ssilerepo@gmail.com). Each plugin's page has these links too, under Contact.

## Thanks

Jellyfin Medic stands on other people's work:

- **[Igor Marques (@camarigor)](https://github.com/camarigor)**, who has contributed dozens of pull requests across Medic, Medic Picks and Medic Profiles: safer track cleanup, the week-long schedule planner, tasks recognised on servers in any language, secrets kept out of the support export, LoadGuard watching the whole server's memory, the Dashboard's layout and data, the release tests and much more. Merged: [#63](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/63), [#64](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/64), [#65](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/65), [#66](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/66), [#67](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/67), [#68](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/68), [#69](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/69), [#70](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/70), [#71](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/71), [#72](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/72), [#73](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/73), [#74](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/74), [#75](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/75), [#76](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/76), [#77](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/77), [#78](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/78), [#79](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/79), [#80](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/80), [#81](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/81), [#82](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/82), [#83](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/83), [#84](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/84), [#85](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/85), [#86](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/86), [#87](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/87), [#88](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/88), [#89](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/89), [#90](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/90), [#91](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/91), [#92](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/92), [#93](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/93), [#94](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/94), [#95](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/95), [#96](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/96), [#97](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/97), [#98](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/98), [#99](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/99), [#100](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/100), [#101](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/101), [#102](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/102), [#103](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/103), [#104](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/104), [#105](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/105), [#106](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/106), [#107](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/107), [#108](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/108), [#117](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/117), [#121](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/121), [#125](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/125), [#130](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/130). Their work also came in through other pull requests: [#111](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/111), [#112](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/112), [#114](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/114), [#115](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/115) through [#113](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/113), and [#119](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/119) through [#122](https://github.com/P1zzm1ssile/Jellyfin-Medic/pull/122).
- **Everyone who sends a pull request.** Each release's notes end with a line thanking the people whose pull requests went into it.
- **The Jellyfin team**, for the server this is built on.
- **The LinuxServer.io team** and **binhex**, whose Jellyfin containers are what Medic is developed and tested against.
- **Unraid (Lime Technology)**, the platform it was built for.
- **The [awesome-jellyfin](https://github.com/awesome-jellyfin/awesome-jellyfin) community project**, whose plugin list powers Medic's Plugin directory tab.
- **[TMDB](https://www.themoviedb.org)**, whose recommendations power Medic Picks' suggestions from outside your library. This product uses the TMDB API but is not endorsed or certified by TMDB.

Thank you to all of them.

## Licence

MIT. See [LICENSE](LICENSE).

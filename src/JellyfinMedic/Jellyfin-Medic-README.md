<p align="center">
  <img src="src/JellyfinMedic/thumb.png" alt="Jellyfin Medic" width="180">
</p>

<h1 align="center">Jellyfin Medic</h1>

<p align="center">Health checks, performance tests and usage-aware task scheduling for your Jellyfin server.</p>

<p align="center">
  <a href="https://github.com/P1zzm1ssile/Jellyfin-Medic/actions/workflows/release.yml"><img src="https://github.com/P1zzm1ssile/Jellyfin-Medic/actions/workflows/release.yml/badge.svg" alt="Build and Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-GPLv3-blue.svg" alt="Licence: GPL v3"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.1%2B-00a4dc.svg" alt="Jellyfin 12.1+">
</p>

---

## What it does

Jellyfin Medic looks after a busy Jellyfin server. It checks your settings, hardware, storage, libraries, schedules, plugins and users, tells you what to change and where, and schedules your maintenance tasks to run when nobody's watching. It's built for large libraries, IPTV and setups with lots of community plugins.

Everything lives on one page in the dashboard sidebar, under **Plugins → Jellyfin Medic**. Only administrators can use it.

### Dashboard
A single screen of tiles: how many things need fixing, tonight's tasks, what the server is doing right now (CPU, memory, who's watching), your top issues, the last 24 hours, users and access, updates waiting, and a maintenance panel to free up disk space, check whether your server is exposed to the internet, or stop running tasks.

### Schedule
A week-at-a-glance timeline. Each task is a coloured block on its day, with shading behind it showing how busy that hour usually is and a red line marking "now". Click any task for its details. Medic works out the quietest times from your own viewing pattern and can run a task at different times on different days; **Preview** shows the plan before you **Apply** it, and every change is backed up so it can be undone. Jellyfin has no monthly trigger, so Medic runs monthly tasks itself.

### Checks
Every finding, most serious first, each with its current value, the suggested value, why it matters and where to change it. Checked something and happy with it? **Ignore** it and it drops out of the counts. Findings come back on their own if the situation changes.

Medic checks, among other things:
- **Hardware and transcoding:** whether your GPU is being used, and set up to match; hardware encoding, decoding and HDR tone mapping; where transcodes are written.
- **Storage:** free space, Unraid user-share paths that slow the database, log and database sizes, debug logging, and disk health from Unraid's own SMART data.
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

### Settings and support
Change Medic's own options, browse every Jellyfin setting, and build a masked copy of your plugin settings to attach when asking for help.

---

## Requirements

- **Jellyfin 12.1 or later.**
- **Server language set to English** (tasks are recognised by their English names).
- Developed and tested on **Unraid** with the linuxserver.io Jellyfin container. Any Linux install should work; the Unraid-specific checks (user shares, SMART) simply skip elsewhere. GPU checks cover NVIDIA, Intel and AMD.

## Installation

### From the plugin repository (recommended)
1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and click **+**.
2. Enter:
   - **Name:** `Jellyfin Medic`
   - **URL:** `https://raw.githubusercontent.com/P1zzm1ssile/Jellyfin-Medic/main/manifest.json`
3. Save, open the **Catalog** tab, install **Jellyfin Medic**, and restart Jellyfin.

### Manual install
Download the latest zip from [Releases](https://github.com/P1zzm1ssile/Jellyfin-Medic/releases), create a `JellyfinMedic` folder in your Jellyfin plugins directory, put `JellyfinMedic.dll` inside it, and restart Jellyfin.

### Moving from Task Advisor or Setup Optimiser
Medic replaces both. On first start it copies their data across (schedule backups, run history, monthly tasks, viewing pattern, ignored findings), then flags the old plugins so you can uninstall them. Nothing is lost.

---

## Privacy

- **Admin only.** Every part of Medic requires an administrator account.
- **Your data stays on your server.** IP addresses and settings are read, shown to you, and never saved into Medic's own files.
- **No passwords are ever read, stored or sent.** Medic deliberately does not test password strength, because that can't be done without handling real passwords.
- **Two features reach the internet, both only when you press the button:** the speed test (Cloudflare) and the "is my server exposed" check, which sends only a port number. Neither sends any of your data.
- **The "help improve Medic" export** masks passwords, keys, tokens, usernames and credentials in links before you ever see the file, and asks you to read it before sending. Sending it is always your choice. If anything personal reaches the maintainer, it's deleted and not used.

---

## Building and releasing

Requires the .NET 10 SDK.

```
dotnet publish src/JellyfinMedic/JellyfinMedic.csproj -c Release -o publish/JellyfinMedic
```

Releases are automatic: push a tag like `v1.0.0` and GitHub Actions builds the plugin, creates the release, and adds it to `manifest.json`.

---

## About this project

I'm an ISO Lead Auditor, so inspecting logs, finding bottlenecks and reviewing where processes fail is what I do every day. I'm not a professional software developer.

I run a busy Jellyfin server at home: a large library, IPTV, and dozens of community plugins. Keeping it healthy meant constant digging, heavy maintenance tasks colliding overnight and causing buffering, runaway plugins pinning the CPU, settings that quietly held the server back, and no easy way to see any of it. Stock Jellyfin gives you very little visibility into how long tasks take or what's dragging things down.

So I built the tools I wanted: first Task Advisor to schedule tasks sensibly, then Setup Optimiser to check the server's health, and finally I merged them into Jellyfin Medic. It brings the same mindset I use in auditing, find the problem, explain it plainly, point at the fix, to a Jellyfin server.

Medic was built with AI assistance for the development, directed and tested by me against my own server and its real problems. I'm sharing it for anyone running something similar.

Issues, suggestions and pull requests are welcome. Translations especially: Medic keeps all its wording in one place so other languages can be added, and I'd rather have them checked by people who speak the language than machine-translated.

## Licence

GNU General Public License v3.0. See [LICENSE](LICENSE).

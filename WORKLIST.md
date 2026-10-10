# Work list (agreed 8 October 2026)

Progress on the requests agreed on 8 October. Each item is ticked when it's committed and pushed.
Anything not ticked has not been done.

| # | Item | Status |
|---|---|---|
| 1 | Test setup: real Jellyfin in Docker with the three plugins, run by `tests/e2e/run.sh` | Done: 32 checks (API, schedule, Picks build, every plugin page and tab in a real browser); also runs on pull requests |
| 2 | Picks: "Update my picks" responds with 30 picks and a genre; "Show me different ones" | Done: Medic Picks 1.2.1 |
| 3 | Admin banners on the home page (serious errors on first sight; restart needed), each with an off switch | Done: Medic 1.0.13 (restart banner not tested end to end: the test server never has an update waiting) |
| 4 | Scheduling: per task, keep current / let Medic schedule / leave unscheduled | Done: Medic 1.0.13 |
| 5 | Scheduling: estimated time left for running tasks | Done: Medic 1.0.13 |
| 6 | Track cleanup: exclude folders, shows or films | Done: Medic 1.0.13 |
| 7 | IPTV: list the duplicate channels; drop the Dispatcharr advice | Done: Medic 1.0.13 |
| 8 | Disk space: every library drive, fill rate, metadata/trickplay/cache sizes | Done: Medic 1.0.13 |
| 9 | GPU advice: more precise settings so the GPU does the heavy work | Done: Medic 1.0.13 (GPU-only checks not run end to end: the test server has no GPU; the CPU-only ones are tested) |
| 10 | Better general performance recommendations | Done: Medic 1.0.13 |
| 11 | Theme checks: custom CSS and theme imports | Done: Medic 1.0.13 (checks the CSS and its addresses; can't see how pages actually draw) |
| 12 | Resource monitoring: Jellyfin's own CPU, RAM, disk, GPU with spike warnings | Done: Medic 1.0.13 (measuring and FFmpeg detection tested; a real spike and GPU readings not tested, as the test server has no GPU and can't be loaded for 45 seconds reliably) |
| 13 | Medic Profiles: indexer status (Sonarr/Radarr, Prowlarr optional) | Done: Medic Profiles 1.1.0, from Sonarr and Radarr (which include Prowlarr's indexers). Not done: a direct Prowlarr connection |
| 14 | Medic Profiles: block .exe and similar downloads (.rar/.zip optional) | Done: Medic Profiles 1.1.0, for qBittorrent 4.6+ (tested against a pretend qBittorrent). Not done: other download clients |
| 15 | Reduce the code | Not done |

Dropped at the owner's request: Unmanic integration.

Already done on this branch (9 October): scheduler uses real 15-minute slots (Medic 1.0.13).

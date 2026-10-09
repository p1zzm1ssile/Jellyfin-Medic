# Work list (agreed 8 October 2026)

Progress on the requests agreed on 8 October. Each item is ticked when it's committed and pushed.
Anything not ticked has not been done.

| # | Item | Status |
|---|---|---|
| 1 | Test setup: real Jellyfin in Docker with the three plugins, run by `tests/e2e/run.sh` | Done: 32 checks (API, schedule, Picks build, every plugin page and tab in a real browser); also runs on pull requests |
| 2 | Picks: "Update my picks" responds with 30 picks and a genre; "Show me different ones" | Not done |
| 3 | Admin banners on the home page (serious errors on first sight; restart needed), each with an off switch | Not done |
| 4 | Scheduling: per task, keep current / let Medic schedule / leave unscheduled | Not done |
| 5 | Scheduling: estimated time left for running tasks | Not done |
| 6 | Track cleanup: exclude folders, shows or films | Not done |
| 7 | IPTV: list the duplicate channels; drop the Dispatcharr advice | Not done |
| 8 | Disk space: every library drive, fill rate, metadata/trickplay/cache sizes | Not done |
| 9 | GPU advice: more precise settings so the GPU does the heavy work | Not done |
| 10 | Better general performance recommendations | Not done |
| 11 | Theme checks: custom CSS and theme imports | Not done |
| 12 | Resource monitoring: Jellyfin's own CPU, RAM, disk, GPU with spike warnings | Not done |
| 13 | Medic Profiles: indexer status (Sonarr/Radarr, Prowlarr optional) | Not done |
| 14 | Medic Profiles: block .exe and similar downloads (.rar/.zip optional) | Not done |
| 15 | Reduce the code | Not done |

Dropped at the owner's request: Unmanic integration.

Already done on this branch (9 October): scheduler uses real 15-minute slots (Medic 1.0.13).

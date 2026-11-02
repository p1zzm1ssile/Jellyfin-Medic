<!-- Thanks for contributing! Please fill in the sections below. -->

## Summary

<!-- What does this pull request do, and why? If it fixes something, say what happened before and what happens now. -->

## Type of change

<!-- Tick one or more. -->

- [ ] Bug fix (nothing that works today changes)
- [ ] New feature (nothing that works today changes)
- [ ] Breaking change (behaviour, settings or saved files change)
- [ ] Security fix
- [ ] Documentation only
- [ ] CI, build or tests only
- [ ] Refactor (no change in behaviour)

## Plugins

- [ ] Jellyfin Medic
- [ ] Medic Picks
- [ ] Medic Profiles
- [ ] The repository itself (CI, scripts, docs)

## Related issues

<!-- e.g. "Fixes #3", or "Part of #12" for one step of a larger change. -->

## How was this tested?

<!-- At the very least: what did you do to convince yourself it works? -->

- [ ] Added or updated unit tests (`dotnet test tests/<project>`)
- [ ] Added or updated e2e checks (`tests/e2e/run.sh` starts a real Jellyfin in Docker and runs them)
- [ ] Tried it on a running Jellyfin server
- [ ] Not applicable, and I say why below

Server tested against: <!-- e.g. Jellyfin 12.2.0 in Docker -->

## Checklist

- [ ] It follows the style of the code around it and builds with warnings as errors
- [ ] Comments only say *why*, where the code doesn't already show it
- [ ] Anything people will notice has an entry under "Unreleased" in that plugin's CHANGELOG
- [ ] The README is updated if what people see or set up has changed
- [ ] Settings and files that servers already have still work (keys files, history, schedules)
- [ ] Keys and passwords never reach the browser, and admin-only routes stay admin-only
- [ ] If `medic.html` changed, its script still parses (`node --check` on what's inside its `<script>` tags)
- [ ] CI passes (unit and e2e)

## Screenshots or log lines

<!-- Optional, and very helpful for anything on a page or anything that changes behaviour. -->

## Anything else

<!-- What reviewers should know: trade-offs, overlap with other open pull requests, follow-ups. -->

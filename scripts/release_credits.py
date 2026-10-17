#!/usr/bin/env python3
"""Prints a line thanking everyone besides the owner whose pull requests went into a release.

The release workflows add it under the changelog section, so the release notes and the manifest's
changelog credit contributors without anyone having to remember:

  python3 scripts/release_credits.py v1.0.20 [--changes CHANGES.md]

It finds the pull requests merged after the previous release of the same plugin (v*, picks-v*,
profiles-v* or film-posters-v*) was published and up to this one, that change that plugin's files or its
tests (the shared code in shared/ and its tests count for both Medic Picks and Medic Profiles, which compile
it). Whoever opened one, other than the repository owner and bots, is thanked with their pull requests:

  Thanks @someone (#117, #121) for their work on this release.

It goes by when each pull request was merged rather than by commits, so a pull request whose
commits reached main another way (through a follow-up pull request that resolved a conflict, or
after the history was rewritten) is still credited.

Someone the changelog section already mentions as @login is left out, so a credit written by hand
isn't repeated. Prints nothing when there is no one to thank. Reads GITHUB_TOKEN (or GH_TOKEN),
GITHUB_REPOSITORY (owner/name) and GITHUB_REPOSITORY_OWNER (the person not thanked) from the
environment, as GitHub Actions sets them; --repo and --owner override the last two.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

import github_http

PICKS = "Jellyfin.Plugin.MedicPicks/"
PROFILES = "Jellyfin.Plugin.MedicProfiles/"
FILM_POSTERS = "Jellyfin.Plugin.MedicPosters/"
PICKS_TESTS = "tests/MedicPicks.Tests/"
PROFILES_TESTS = "tests/MedicProfiles.Tests/"
FILM_POSTERS_TESTS = "tests/MedicPosters.Tests/"
# Code both Medic Picks and Medic Profiles compile in (shared/Medic.Shared into each DLL, tests/Shared into each
# one's tests), with its own tests, so a change to it goes out in both.
PICKS_AND_PROFILES = ("shared/", "tests/MedicShared.Tests/", "tests/Shared/")
# Files the release workflows write to main for every plugin, which say nothing about who did the work.
SHARED = {"manifest.json"}


def plugin_prefix(tag: str) -> str:
    for prefix in ("picks-v", "profiles-v", "film-posters-v", "v"):
        if tag.startswith(prefix) and re.fullmatch(r"[0-9]+(\.[0-9]+)*", tag[len(prefix):]):
            return prefix
    raise ValueError(f"{tag} isn't a release tag (v1.2.3, picks-v1.2.3, profiles-v1.2.3 or film-posters-v1.2.3)")


def belongs_to(prefix: str, path: str) -> bool:
    """Whether a changed file is part of the plugin that the tag prefix releases."""
    if path in SHARED:
        return False
    if prefix == "picks-v":
        return path.startswith((PICKS, PICKS_TESTS, *PICKS_AND_PROFILES))
    if prefix == "profiles-v":
        return path.startswith((PROFILES, PROFILES_TESTS, *PICKS_AND_PROFILES))
    if prefix == "film-posters-v":
        return path.startswith((FILM_POSTERS, FILM_POSTERS_TESTS))
    # Jellyfin Medic is everything else: its code, tests, docs and the build.
    return not path.startswith((PICKS, PICKS_TESTS, PROFILES, PROFILES_TESTS, *PICKS_AND_PROFILES, FILM_POSTERS,
                                FILM_POSTERS_TESTS))


def version_key(version: str):
    return tuple(int(p) for p in version.split("."))


def previous_tag(tag: str, tags: list[str]) -> str | None:
    """The newest tag of the same plugin with a lower version, or None for its first release."""
    prefix = plugin_prefix(tag)
    current = version_key(tag[len(prefix):])
    older = []
    for t in tags:
        try:
            if plugin_prefix(t) == prefix and version_key(t[len(prefix):]) < current:
                older.append(t)
        except ValueError:
            continue
    return max(older, key=lambda t: version_key(t[len(prefix):]), default=None)


def is_bot(login: str, kind: str = "") -> bool:
    return kind == "Bot" or login.endswith("[bot]")


def in_window(merged_at: str | None, after: str | None, until: str) -> bool:
    """Whether a pull request merged after the previous release and by this one (ISO 8601, UTC)."""
    return bool(merged_at) and (after is None or merged_at > after) and merged_at <= until


def contributors(pulls: list[dict], owner: str) -> dict[str, set[int]]:
    """Who to thank, with their pull requests. Each pull request is {"number", "login", "type"}."""
    owner = owner.lower()
    people: dict[str, set[int]] = {}
    for pr in pulls:
        login = pr["login"]
        if login.lower() != owner and not is_bot(login, pr.get("type", "")):
            people.setdefault(login, set()).add(pr["number"])
    return people


def credit_line(people: dict[str, set[int]], changes: str = "") -> str:
    mentioned = {m.lower() for m in re.findall(r"@([A-Za-z0-9-]+)", changes)}
    names = []
    for login in sorted(people, key=str.lower):
        if login.lower() in mentioned:
            continue
        prs = ", ".join(f"#{n}" for n in sorted(people[login]))
        names.append(f"@{login} ({prs})")
    if not names:
        return ""
    who = names[0] if len(names) == 1 else ", ".join(names[:-1]) + " and " + names[-1]
    return f"Thanks {who} for their work on this release."


def git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def github(path: str) -> object:
    """GET from the GitHub API; None when it isn't there. Tries three times, as the connection drops at times."""
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    request = urllib.request.Request(f"https://api.github.com{path}", headers={
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        **({"Authorization": f"Bearer {token}"} if token else {}),
    })
    for attempt in range(3):
        try:
            with github_http.urlopen(request, timeout=30) as response:
                return json.load(response)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            if attempt == 2:
                raise
            time.sleep(2 * (attempt + 1))
    return None


def pages(path: str):
    sep = "&" if "?" in path else "?"
    page = 1
    while True:
        items = github(f"{path}{sep}per_page=100&page={page}") or []
        yield from items
        if len(items) < 100:
            return
        page += 1


def published(repo: str, tag: str) -> str | None:
    """When a tag's release was published, in the API's format (2026-10-15T15:20:19Z).

    The release, not the tagged commit: a pull request is marked merged when its commits are pushed,
    which can be after they were committed, so going by the commit would move it to the next release.
    """
    release = github(f"/repos/{repo}/releases/tags/{tag}")
    return (release or {}).get("published_at") or (release or {}).get("created_at")


def fetch_pulls(tag: str, repo: str, owner: str) -> list[dict]:
    """Other people's pull requests merged for this release that change its plugin."""
    prefix = plugin_prefix(tag)
    tags = git("tag", "--list").split()
    previous = previous_tag(tag, tags)
    after = published(repo, previous) if previous else None
    # A tag without a release (it failed or was deleted) thanked nobody, so count from the release before it.
    skipped = []
    while previous and not after:
        skipped.append(previous)
        previous = previous_tag(previous, tags)
        after = published(repo, previous) if previous else None
    if skipped:
        names = ", ".join(skipped)
        if previous:
            note = f"No release for {names}: the credits start at {previous}'s release ({after})"
        else:
            note = f"No release for {names} or earlier: every merged pull request counts"
        print(note, file=sys.stderr)
    # While the release is being made it isn't published yet, so everything merged until now counts.
    until = published(repo, tag) or "9999"

    pulls = []
    for pr in pages(f"/repos/{repo}/pulls?state=closed&sort=updated&direction=desc"):
        login, kind = pr["user"]["login"], pr["user"].get("type", "")
        if login.lower() == owner.lower() or is_bot(login, kind):
            continue
        if not in_window(pr.get("merged_at"), after, until):
            continue
        files = [f["filename"] for f in pages(f"/repos/{repo}/pulls/{pr['number']}/files")]
        if any(belongs_to(prefix, f) for f in files):
            pulls.append({"number": pr["number"], "login": login, "type": kind})
    return pulls


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("tag")
    ap.add_argument("--changes", help="the changelog section, so people it already credits are left out")
    ap.add_argument("--owner", default=os.environ.get("GITHUB_REPOSITORY_OWNER", ""))
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    args = ap.parse_args()
    if not args.repo or not args.owner:
        sys.exit("Set GITHUB_REPOSITORY and GITHUB_REPOSITORY_OWNER, or pass --repo and --owner")

    changes = ""
    if args.changes:
        with open(args.changes, encoding="utf-8") as f:
            changes = f.read()
    line = credit_line(contributors(fetch_pulls(args.tag, args.repo, args.owner), args.owner), changes)
    if line:
        print(line)


if __name__ == "__main__":
    main()

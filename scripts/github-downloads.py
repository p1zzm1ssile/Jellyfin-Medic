#!/usr/bin/env python3
"""Logs download counts for everything a GitHub user publishes and sends a summary to Telegram.

Counts two things:
  - container images (ghcr.io): total pulls, read off each public package page,
    because GitHub's packages API doesn't expose download counts
  - release downloads: the download_count of every release asset, per repo, split per plugin
    when a repo releases several under different tag prefixes

Usage: github-downloads.py <user> <csv file>
Env: GITHUB_TOKEN (optional, raises the API rate limit),
     TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID (to send the message),
     EXTRA_PACKAGES (optional, space-separated package names the profile page misses).
"""
import csv
import json
import os
import re
import sys
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import github_http

UK = ZoneInfo("Europe/London")
TIME_FORMAT = "%Y-%m-%d %H:%M:%S"  # the CSV keeps UTC times


def fetch(url, api=False):
    headers = {"User-Agent": "github-downloads"}
    if api:
        headers["Accept"] = "application/vnd.github+json"
        if os.environ.get("GITHUB_TOKEN"):
            headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    with github_http.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as r:
        body = r.read().decode()
    return json.loads(body) if api else body


def api_pages(url):
    page = 1
    while True:
        items = fetch(f"{url}{'&' if '?' in url else '?'}per_page=100&page={page}", api=True)
        yield from items
        if len(items) < 100:
            return
        page += 1


def packages(user):
    html = fetch(f"https://github.com/{user}?tab=packages")
    names = re.findall(r'/packages/container/package/([A-Za-z0-9._%-]+)"', html)
    names += os.environ.get("EXTRA_PACKAGES", "").split()
    return sorted(set(urllib.parse.unquote(n) for n in names))


def package_downloads(user, name):
    html = fetch(f"https://github.com/users/{user}/packages/container/package/{urllib.parse.quote(name, safe='')}")
    m = re.search(r'Total downloads</span>\s*<h3 title="(\d+)"', html)
    if not m:
        raise ValueError("no download count on the package page")
    return int(m.group(1))


def release_product(release):
    """Which product a release belongs to, from its tag: picks-v1.2.4 -> picks, v1.0.22 -> ''."""
    m = re.match(r"(.*?)-?v?\d", release["tag_name"])
    return m.group(1) if m else release["tag_name"]


def product_label(repo, prefix, releases):
    """A readable name for one product in a repo, taken from its asset names:
    MedicPicks-v1.2.4.zip -> Medic Picks. Falls back to the repo name and tag prefix."""
    for r in releases:
        for a in r["assets"]:
            stem = re.split(r"[-_. ]v?\d", a["name"])[0]
            if stem:
                return re.sub(r"(?<=[a-z])(?=[A-Z])", " ", stem)
    return f"{repo} {prefix}".strip()


def release_downloads(user):
    """Downloads per product. A repo that releases several plugins under different tag
    prefixes (v1.x, picks-v1.x, profiles-v1.x...) gets one line per plugin."""
    counts = {}
    for repo in api_pages(f"https://api.github.com/users/{user}/repos?type=owner"):
        if repo["fork"]:
            continue
        groups = {}
        for r in api_pages(f"https://api.github.com/repos/{user}/{repo['name']}/releases"):
            if r["assets"]:  # a release with nothing to download would only add a line stuck at 0
                groups.setdefault(release_product(r), []).append(r)
        for prefix, releases in groups.items():
            name = repo["name"] if len(groups) == 1 else product_label(repo["name"], prefix, releases)
            counts[name] = counts.get(name, 0) + sum(a["download_count"] for r in releases for a in r["assets"])
    return counts


def history(csv_path):
    """Every count logged so far, per line, oldest first, with its time in UK time."""
    rows = {}
    if os.path.exists(csv_path):
        with open(csv_path, newline="") as f:
            for row in csv.DictReader(f):
                when = datetime.strptime(row["Timestamp"], TIME_FORMAT).replace(tzinfo=timezone.utc).astimezone(UK)
                rows.setdefault((row["Kind"], row["Name"]), []).append((when, int(row["Downloads"])))
    for logged in rows.values():
        logged.sort(key=lambda r: r[0])
    return rows


def day_start(logged, day):
    """A line's count at the first run on `day` (UK date), or at the last run before it
    when nothing ran that day. None when the line was first seen after `day`."""
    before = None
    for when, n in logged:
        if when.date() == day:
            return n
        if when.date() > day:
            return before
        before = n
    return before


def build_message(counts, errors, hist, now):
    """The Telegram summary. Each line shows the change since the last run. The total shows
    the change since midnight, and the first run of a new day shows the whole of the day
    before instead. Days are counted from the first run after midnight rather than the clock,
    because GitHub often starts scheduled runs late."""
    now = now.astimezone(UK)
    today = now.date()
    last_run = max((logged[-1][0] for logged in hist.values()), default=None)
    first_today = last_run is None or last_run.date() != today
    day = today - timedelta(days=1) if first_today else today

    def line(kind, name, n):
        logged = hist.get((kind, name))
        return f"• {name}: {n:,}" + (f" (+{n - logged[-1][1]:,})" if logged else " (new)")

    lines = [f"📊 GitHub downloads, {now:%a %d %b %Y %H:%M}"]
    if last_run:
        lines.append("Changes since the last run at " + (
            f"{last_run:%H:%M}" if last_run.date() == today else f"{last_run:%a %d %b %H:%M}"))
    for kind, title in (("image", "📦 Container images"), ("release", "🏷️ Release downloads")):
        rows = [line(*c) for c in counts if c[0] == kind]
        if rows:
            lines += ["", title] + rows

    total = sum(n for _, _, n in counts)
    start = 0
    for kind, name, n in counts:
        at_start = day_start(hist.get((kind, name), []), day)
        start += n if at_start is None else at_start  # a line new since then adds nothing
    if not hist:
        lines += ["", f"Total: {total:,}"]
    elif first_today:
        lines += ["", f"Total: {total:,} (+{total - start:,} for the whole of {day:%a %d %b})"]
    else:
        lines += ["", f"Total: {total:,} (+{total - start:,} today since 00:00)"]
    if errors:
        lines += ["", "⚠️ Couldn't read:"] + [f"• {e}" for e in errors]
    return "\n".join(lines)


def send_telegram(text):
    token, chat = os.environ.get("TELEGRAM_BOT_TOKEN"), os.environ.get("TELEGRAM_CHAT_ID")
    if not token or not chat:
        print("TELEGRAM_BOT_TOKEN or TELEGRAM_CHAT_ID not set, skipping Telegram", file=sys.stderr)
        return
    data = urllib.parse.urlencode({"chat_id": chat, "text": text}).encode()
    urllib.request.urlopen(f"https://api.telegram.org/bot{token}/sendMessage", data, timeout=30).read()


def main():
    user, csv_path = sys.argv[1], sys.argv[2]
    now = datetime.now(timezone.utc)
    hist = history(csv_path)
    counts, errors = [], []

    try:
        for name in packages(user):
            try:
                counts.append(("image", name, package_downloads(user, name)))
            except Exception as e:
                errors.append(f"image {name}: {e}")
    except Exception as e:
        errors.append(f"listing packages: {e}")

    try:
        counts += [("release", name, n) for name, n in sorted(release_downloads(user).items())]
    except Exception as e:
        errors.append(f"release downloads: {e}")

    if not counts:
        print("\n".join(errors) or "Nothing to count", file=sys.stderr)
        sys.exit(1)

    os.makedirs(os.path.dirname(csv_path) or ".", exist_ok=True)
    new_file = not os.path.exists(csv_path)
    with open(csv_path, "a", newline="") as f:
        w = csv.writer(f)
        if new_file:
            w.writerow(["Timestamp", "Kind", "Name", "Downloads"])
        for kind, name, n in counts:
            w.writerow([now.strftime(TIME_FORMAT), kind, name, n])

    message = build_message(counts, errors, hist, now)
    print(message)
    send_telegram(message)


if __name__ == "__main__":
    main()

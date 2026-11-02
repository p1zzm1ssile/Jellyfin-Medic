#!/usr/bin/env python3
"""Sends a Telegram message for every new issue opened on any repo a GitHub user owns.

Looks back a day each run and remembers which issues it has already sent in a small JSON
file, so a late scheduled run or GitHub's search lagging behind never loses or repeats one.
The first run only notes what is already there, so it doesn't send a burst of old issues.
Issues the user opened themselves and pull requests are left out.

Usage: github-issues.py <user> <state file>
Env: GITHUB_TOKEN (optional, raises the API rate limit),
     TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID (to send the messages).
"""
import json
import os
import sys
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

import github_http

LOOK_BACK = timedelta(days=1)


def search(query):
    headers = {"User-Agent": "github-issues", "Accept": "application/vnd.github+json"}
    if os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    url = f"https://api.github.com/search/issues?q={urllib.parse.quote(query)}&sort=created&order=asc&per_page=100"
    with github_http.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as r:
        return json.loads(r.read().decode())["items"]


def new_issues(user, since):
    return search(f"user:{user} is:issue created:>={since:%Y-%m-%dT%H:%M:%SZ}")


def message(issue):
    repo = issue["repository_url"].rsplit("/", 1)[1]
    return (f"🐛 New issue in {repo}\n"
            f"#{issue['number']} {issue['title']}\n"
            f"by {issue['user']['login']}\n"
            f"{issue['html_url']}")


def to_send(issues, seen, user):
    """The issues to send now: not sent before, and not opened by the user themselves."""
    return [i for i in issues if str(i["id"]) not in seen and i["user"]["login"].lower() != user.lower()]


def send_telegram(text):
    token, chat = os.environ.get("TELEGRAM_BOT_TOKEN"), os.environ.get("TELEGRAM_CHAT_ID")
    if not token or not chat:
        print("TELEGRAM_BOT_TOKEN or TELEGRAM_CHAT_ID not set, skipping Telegram", file=sys.stderr)
        return
    data = urllib.parse.urlencode({"chat_id": chat, "text": text, "disable_web_page_preview": "true"}).encode()
    urllib.request.urlopen(f"https://api.telegram.org/bot{token}/sendMessage", data, timeout=30).read()


def main():
    user, state_path = sys.argv[1], sys.argv[2]
    now = datetime.now(timezone.utc)
    first_run = not os.path.exists(state_path)
    seen = {} if first_run else json.load(open(state_path))

    issues = new_issues(user, now - LOOK_BACK)
    for issue in ([] if first_run else to_send(issues, seen, user)):
        text = message(issue)
        print(text, end="\n\n")
        send_telegram(text)
        seen[str(issue["id"])] = issue["created_at"]
    for issue in issues:
        seen.setdefault(str(issue["id"]), issue["created_at"])

    # Only what the next look-back can still return needs remembering.
    cutoff = (now - 2 * LOOK_BACK).strftime("%Y-%m-%dT%H:%M:%SZ")
    seen = {k: v for k, v in seen.items() if v >= cutoff}
    os.makedirs(os.path.dirname(state_path) or ".", exist_ok=True)
    with open(state_path, "w") as f:
        json.dump(seen, f, indent=1, sort_keys=True)
    if first_run:
        print(f"First run: noted {len(issues)} recent issues without sending them")


if __name__ == "__main__":
    main()

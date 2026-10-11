#!/bin/bash
# Logs the total download count of a GitHub container package and sends it to Telegram.
# GitHub's packages API has no download counts, so this reads the number off the public package page.
# Usage: package-downloads.sh <owner> <package> <csv file>
# Needs TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID in the environment to send the message.
set -euo pipefail

OWNER="$1"
PACKAGE="$2"
CSV="$3"
PAGE="https://github.com/users/$OWNER/packages/container/package/$PACKAGE"

HTML=$(curl -fsSL "$PAGE")
# The page shows: <span ...>Total downloads</span> <h3 title="1234">1.23K</h3>
TOTAL=$(printf '%s' "$HTML" | tr '\n' ' ' \
  | grep -oE 'Total downloads</span>[[:space:]]*<h3 title="[0-9]+"' \
  | grep -oE '[0-9]+"$' | tr -d '"' || true)
if [ -z "$TOTAL" ]; then
  echo "Couldn't find the download count on $PAGE" >&2
  exit 1
fi

mkdir -p "$(dirname "$CSV")"
[ -f "$CSV" ] || echo "Timestamp,Package,Downloads" > "$CSV"
PREV=$(tail -n 1 "$CSV" | cut -d, -f3)
[[ "$PREV" =~ ^[0-9]+$ ]] || PREV=""

NOW=$(date -u '+%Y-%m-%d %H:%M:%S')
echo "$NOW,$PACKAGE,$TOTAL" >> "$CSV"

MSG="📦 $PACKAGE: $TOTAL total downloads"
[ -n "$PREV" ] && MSG="$MSG (+$((TOTAL - PREV)) since last check)"
echo "$MSG"

if [ -n "${TELEGRAM_BOT_TOKEN:-}" ] && [ -n "${TELEGRAM_CHAT_ID:-}" ]; then
  curl -fsS -o /dev/null "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/sendMessage" \
    --data-urlencode "chat_id=$TELEGRAM_CHAT_ID" \
    --data-urlencode "text=$MSG"
else
  echo "TELEGRAM_BOT_TOKEN or TELEGRAM_CHAT_ID not set, skipping Telegram" >&2
fi

#!/usr/bin/env bash
# End-to-end tests: builds the three plugins, starts a real Jellyfin in Docker with them
# installed and some sample films, then runs e2e.mjs against it.
#
#   tests/e2e/run.sh            build, start, test, stop
#   KEEP=1 tests/e2e/run.sh     leave Jellyfin running afterwards on http://127.0.0.1:8096
#   tests/e2e/run.sh "dashboard" "memory"    run only the checks whose names contain one of these
#   JELLYFIN_IMAGE=jellyfin/jellyfin:12.1 tests/e2e/run.sh    test against another Jellyfin
#   E2E_NAME=medic-e2e-2 PORT=8097 FAKE_PORT_OFFSET=100 WORK=/tmp/e2e-2 tests/e2e/run.sh
#                               a second run beside the first, with its logs in /tmp/e2e-2/logs
#
# Build output, Jellyfin's logs and the container's output are kept in $LOGS (tests/e2e/.logs by default,
# $WORK/logs when WORK is set).
#
# Needs: dotnet 10 SDK, docker, ffmpeg, node with playwright (PLAYWRIGHT_MODULE can point at it).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# A run given its own WORK keeps its logs there too, so it doesn't delete or overwrite another run's.
if [ -n "${WORK:-}" ]; then LOGS="${LOGS:-$WORK/logs}"; else LOGS="${LOGS:-$ROOT/tests/e2e/.logs}"; fi
WORK="${WORK:-$ROOT/tests/e2e/.work}"
# A fixed image (Jellyfin 12.2.0), so a run only changes when the code or this line does.
IMAGE="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12.2@sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037}"
NAME="${E2E_NAME:-medic-e2e}"
PORT="${PORT:-8096}"

# A container left by a KEEP=1 run goes first: it holds $WORK, and what it wrote there belongs to root, so it's
# handed back (by a container, as only root can) before $WORK is deleted.
docker rm -f "$NAME" >/dev/null 2>&1 || true
if [ -d "$WORK" ] && ! rm -rf "$WORK" 2>/dev/null; then
  docker run --rm --entrypoint chown -v "$WORK:/work" "$IMAGE" -R "$(id -u):$(id -g)" /work
  rm -rf "$WORK"
fi
rm -rf "$LOGS"
mkdir -p "$WORK/config/plugins" "$WORK/cache" "$WORK/media/movies" "$LOGS"

# timeout puts its command in a process group of its own and kills the whole group when the time is up, Chromium
# included. That group doesn't get the terminal's Ctrl+C, and bash only runs a trap once a command in the
# foreground has ended, so the command runs in the background, where wait can be interrupted, and Ctrl+C (or a
# TERM) is handed to timeout, which passes it on to its whole group.
limited() { # seconds, command...
  timeout "$@" &
  limited_pid=$!
  local status=0
  wait "$limited_pid" || status=$?
  limited_pid=""
  return "$status"
}
# shellcheck disable=SC2317 # run by a trap
stop() { # exit status
  trap '' INT TERM
  if [ -n "${limited_pid:-}" ]; then
    kill -TERM "$limited_pid" 2>/dev/null || true
    wait "$limited_pid" 2>/dev/null || true
  fi
  exit "$1"
}
trap 'stop 130' INT
trap 'stop 143' TERM

echo "== Building plugins"
build() { # project, folder, dll
  # MSBuild prints errors on stdout, so the output is kept and shown when the build fails. Its obj and bin folders
  # go under $WORK too, as a second run building the same projects at once would collide in the repository's.
  if ! limited 600 dotnet publish "$ROOT/$1" -c Release -o "$WORK/build/$2" --artifacts-path "$WORK/artifacts" \
    -v quiet -nologo >"$LOGS/build-$2.log" 2>&1; then
    cat "$LOGS/build-$2.log"
    echo "Building $1 failed (output above, and in $LOGS/build-$2.log)" >&2
    exit 1
  fi
  mkdir -p "$WORK/config/plugins/$2"
  cp "$WORK/build/$2/$3" "$WORK/config/plugins/$2/"
}
build src/JellyfinMedic/JellyfinMedic.csproj JellyfinMedic JellyfinMedic.dll
build Jellyfin.Plugin.MedicPicks/Jellyfin.Plugin.MedicPicks.csproj MedicPicks Jellyfin.Plugin.MedicPicks.dll
build Jellyfin.Plugin.MedicProfiles/Jellyfin.Plugin.MedicProfiles.csproj MedicProfiles Jellyfin.Plugin.MedicProfiles.dll

echo "== Making sample films"
GENRES=(Action Comedy Drama Horror Animation Thriller Romance Documentary)
for i in $(seq 1 12); do
  year=$((1990 + i)); dir="$WORK/media/movies/Sample $i ($year)"; mkdir -p "$dir"
  ffmpeg -loglevel error -f lavfi -i "testsrc=size=320x180:rate=10:duration=4" -f lavfi -i "sine=frequency=$((200 + i * 20)):duration=4" \
    -c:v libx264 -preset ultrafast -c:a aac -metadata:s:a:0 language=eng "$dir/Sample $i ($year).mkv"
  g1=${GENRES[$((i % 8))]}; g2=${GENRES[$(((i + 3) % 8))]}
  # One director shared by two films the Picks check watches and one it expects to rank higher for it.
  director=""
  case $i in 1 | 2 | 11) director="<director>Ada Example</director>" ;; esac
  cat > "$dir/Sample $i ($year).nfo" <<NFO
<?xml version="1.0" encoding="utf-8"?>
<movie><title>Sample $i</title><year>$year</year><genre>$g1</genre><genre>$g2</genre>$director<lockdata>true</lockdata></movie>
NFO
done
# Two films with a French audio track that track cleanup would remove.
for t in "Two Tracks (2015)" "Keep Me Too (2016)"; do
  mkdir -p "$WORK/media/movies/$t"
  ffmpeg -loglevel error -f lavfi -i "testsrc=size=320x180:rate=10:duration=4" -f lavfi -i "sine=frequency=300:duration=4" -f lavfi -i "sine=frequency=500:duration=4" \
    -map 0 -map 1 -map 2 -c:v libx264 -preset ultrafast -c:a aac -metadata:s:a:0 language=eng -metadata:s:a:1 language=fre "$WORK/media/movies/$t/$t.mkv"
done
# A longer film, so a transcode runs long enough to be seen.
mkdir -p "$WORK/media/movies/Long Film (2019)"
ffmpeg -loglevel error -f lavfi -i "testsrc2=size=1280x720:rate=25:duration=900" -f lavfi -i "sine=duration=900" \
  -c:v libx264 -preset ultrafast -c:a aac "$WORK/media/movies/Long Film (2019)/Long Film (2019).mkv"
# One IPTV-style .strm entry, which track cleanup must never touch.
mkdir -p "$WORK/media/movies/Stream Film (2020)"
echo "http://127.0.0.1:9/stream.ts" > "$WORK/media/movies/Stream Film (2020)/Stream Film (2020).strm"

# Live TV channels for the IPTV checks, some shown in several qualities.
mkdir -p "$WORK/media/livetv"
{
  echo "#EXTM3U"
  n=0
  for ch in "BBC One" "BBC One HD" "UK: BBC One FHD" "Sky News" "Sky News HD" "Channel 4" "Film4" "Film4 +1" "Dave"; do
    n=$((n + 1)); echo "#EXTINF:-1 tvg-id=\"c$n\" tvg-name=\"$ch\",$ch"; echo "http://127.0.0.1:9/$n.ts"
  done
} > "$WORK/media/livetv/channels.m3u"

# The tests reach Jellyfin through Docker's gateway. Trusting it as a proxy lets a test set the client's
# address with X-Forwarded-For; Jellyfin only reads its known proxies when it starts, so it's set here.
GATEWAY="$(docker network inspect bridge --format '{{range .IPAM.Config}}{{.Gateway}}{{end}}' 2>/dev/null || true)"
if [ -n "$GATEWAY" ]; then
  mkdir -p "$WORK/config/config"
  cat > "$WORK/config/config/network.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <KnownProxies>
    <string>$GATEWAY</string>
  </KnownProxies>
</NetworkConfiguration>
XML
fi

echo "== Starting Jellyfin ($IMAGE)"
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -p "$PORT:8096" --add-host=host.docker.internal:host-gateway \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media" "$IMAGE" >/dev/null
# Jellyfin runs as root in the container, so what it writes under $WORK belongs to root. It's handed back
# before the container goes, or the next run couldn't delete $WORK and would stop before any test.
# shellcheck disable=SC2317 # run by a trap
cleanup() {
  docker logs "$NAME" >"$LOGS/container.log" 2>&1 || true
  cp -r "$WORK/config/log" "$LOGS/jellyfin" 2>/dev/null || true
  [ "${KEEP:-0}" = 1 ] && return
  docker exec "$NAME" chown -R "$(id -u):$(id -g)" /config /cache /media >/dev/null 2>&1 || true
  docker rm -f "$NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# Jellyfin first answers from a small startup server, whose /System/Info/Public already says 200, then swaps
# to the real one, dropping connections on the way. /Startup/Configuration, the tests' first call, only
# answers 200 from the real server.
for i in $(seq 1 90); do
  curl -sf -m 5 "http://127.0.0.1:$PORT/Startup/Configuration" >/dev/null && break
  sleep 2
done

status=0
# The pretend Sonarr, qBittorrent and Seerr listen only where the container reaches this machine.
# A time limit, so a check that hangs fails the run instead of holding it until the CI job is killed.
JF="http://127.0.0.1:$PORT" WORK="$WORK" E2E_NAME="$NAME" FAKE_HOST="${GATEWAY:-0.0.0.0}" limited "${E2E_TIMEOUT:-1500}" node "$ROOT/tests/e2e/e2e.mjs" "$@" || status=$?
[ "$status" = 124 ] && echo "The checks didn't finish within ${E2E_TIMEOUT:-1500} seconds" >&2
docker logs "$NAME" 2>&1 | grep -E "\[ERR\]|\[FTL\]" | grep -iE "medic|picks|profiles" | head -20 || true
exit $status

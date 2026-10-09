#!/usr/bin/env bash
# End-to-end tests: builds the three plugins, starts a real Jellyfin in Docker with them
# installed and some sample films, then runs e2e.mjs against it.
#
#   tests/e2e/run.sh            build, start, test, stop
#   KEEP=1 tests/e2e/run.sh     leave Jellyfin running afterwards on http://127.0.0.1:8096
#
# Needs: dotnet 10 SDK, docker, ffmpeg, node with playwright (PLAYWRIGHT_MODULE can point at it).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK:-$ROOT/tests/e2e/.work}"
IMAGE="${JELLYFIN_IMAGE:-jellyfin/jellyfin:latest}"
NAME=medic-e2e
PORT="${PORT:-8096}"

rm -rf "$WORK"
mkdir -p "$WORK/config/plugins" "$WORK/cache" "$WORK/media/movies"

echo "== Building plugins"
build() { # project, folder, dll
  dotnet publish "$ROOT/$1" -c Release -o "$WORK/build/$2" -v quiet -nologo >/dev/null
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
  cat > "$dir/Sample $i ($year).nfo" <<NFO
<?xml version="1.0" encoding="utf-8"?>
<movie><title>Sample $i</title><year>$year</year><genre>$g1</genre><genre>$g2</genre><lockdata>true</lockdata></movie>
NFO
done
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

echo "== Starting Jellyfin ($IMAGE)"
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -p "$PORT:8096" \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media" "$IMAGE" >/dev/null
cleanup() { [ "${KEEP:-0}" = 1 ] || docker rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT

for i in $(seq 1 90); do
  curl -sf "http://127.0.0.1:$PORT/System/Info/Public" >/dev/null && break
  sleep 2
done

status=0
JF="http://127.0.0.1:$PORT" WORK="$WORK" node "$ROOT/tests/e2e/e2e.mjs" || status=$?
docker logs "$NAME" 2>&1 | grep -E "\[ERR\]|\[FTL\]" | grep -iE "medic|picks|profiles" | head -20 || true
exit $status

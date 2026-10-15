#!/usr/bin/env bash
# Builds the Flutter app for the web and puts it where the API serves it, at /app/.
#
# The result is an installable PWA: open https://<host>/app/ on a phone and add it to the home
# screen. The app talks to whichever host served it, so the same build works on localhost,
# through a tunnel and in production.
#
#   ./scripts/build-pwa.sh

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/src/AuctionPlatform.Api/wwwroot/app"

cd "$root/mobile"

# Flutter leaves earlier output in build/web; clearing it keeps stale files out of the copy.
rm -rf build/web

# MSYS_NO_PATHCONV stops Git Bash on Windows from rewriting "/app/" into a Windows path.
# Other shells ignore it.
# Flutter's own service worker no longer caches anything, so it is switched off and
# web/sw.js takes over.
MSYS_NO_PATHCONV=1 flutter build web --release --base-href=/app/ --pwa-strategy=none

# Emptied rather than deleted, so a file dropped from the build does not linger in what is served.
mkdir -p "$output"
rm -rf "${output:?}"/*
cp -r "$root/mobile/build/web/." "$output/"

# Flutter still writes a service worker of its own that nothing registers; it would only
# confuse anyone reading what is served.
rm -f "$output/flutter_service_worker.js"

# Compressed once here rather than on every request: the app is megabytes of JavaScript and
# WebAssembly, and Brotli at its highest setting takes far too long to do per request. The API
# hands over the .br (or .gz) copy to any browser that accepts it.
find "$output" -type f   \( -name '*.js' -o -name '*.wasm' -o -name '*.json' -o -name '*.css' -o -name '*.html'      -o -name '*.svg' -o -name '*.otf' -o -name '*.ttf' -o -name '*.map' \) -size +1k |
while read -r file; do
  if command -v brotli >/dev/null 2>&1; then brotli -f -q 11 -o "$file.br" "$file"; fi
  gzip -9 -f -k -c "$file" > "$file.gz"
done

# A fresh cache name per build, so an update replaces the old files instead of sitting behind them.
build_id="$(date +%Y%m%d%H%M%S)"
sed -i.bak "s/__BUILD_ID__/$build_id/" "$output/sw.js" && rm -f "$output/sw.js.bak"

echo "PWA built into $output (build $build_id) — serve the API and open /app/"

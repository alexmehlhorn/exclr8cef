#!/usr/bin/env bash
# Compare the version pinned in cef.json against the latest stable on
# Spotify's CEF build CDN. Prints both versions; exits 0 if they match,
# 1 if a newer stable is available (intended for use as a CI gate that
# decides whether to open a bump PR).
#
#   $ scripts/check-cef-upstream.sh
#   pinned     : 147.0.10+gd58e84d+chromium-147.0.7727.118
#   upstream   : 147.0.10+gd58e84d+chromium-147.0.7727.118
#   ✓ up to date
#
# Set GITHUB_OUTPUT (or call with --github-output) to emit the version
# to GitHub Actions outputs.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CEF_JSON="${REPO_ROOT}/cef.json"
INDEX_URL="https://cef-builds.spotifycdn.com/index.json"
# Upstream version is determined from this platform's stable channel —
# all platforms publish the same version simultaneously, so any will do.
PROBE_PLATFORM="${PROBE_PLATFORM:-macosarm64}"

PINNED="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${CEF_JSON}")"

UPSTREAM="$(curl -fsSL --max-time 30 "${INDEX_URL}" \
  | python3 -c '
import json, sys
data = json.load(sys.stdin)
plat = sys.argv[1]
versions = data.get(plat, {}).get("versions", [])
stable = next((v for v in versions if v.get("channel") == "stable"), None)
if not stable:
    sys.exit(2)
print(stable["cef_version"])
' "${PROBE_PLATFORM}")"

echo "pinned    : ${PINNED}"
echo "upstream  : ${UPSTREAM}"

# Emit GitHub Actions outputs if running in CI.
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "pinned=${PINNED}"
    echo "upstream=${UPSTREAM}"
    if [ "${PINNED}" = "${UPSTREAM}" ]; then
      echo "needs_bump=false"
    else
      echo "needs_bump=true"
    fi
  } >> "${GITHUB_OUTPUT}"
fi

if [ "${PINNED}" = "${UPSTREAM}" ]; then
  echo "✓ up to date"
  exit 0
fi
echo "↑ upstream is newer"
exit 1

#!/usr/bin/env bash
# Confirms a deployed barakoCMS is running one specific build, by commit sha.
#
#   scripts/check-deployed-build.sh https://playground.baryo.dev/barakocms-api <sha>
#
# The release used to prove its deploy by asking for a 200 and, at most, reading a
# version string back. Neither distinguishes builds. A version string is the same
# characters for every build of that version, and a 200 is what the previous build
# returns too, so a deploy that pulled nothing and restarted nothing passed both.
#
# /health/build answers with the commit the image was built from. This compares it
# to the commit being released and fails on anything else, including the "unknown"
# an image built without BARAKO_BUILD_SHA reports and the 404 an older build gives.
# Failing on those is deliberate: not knowing what is deployed is not evidence that
# the right thing is.
set -euo pipefail

BASE="${1:?usage: check-deployed-build.sh <api-base-url> <expected-sha>}"
EXPECTED="${2:?usage: check-deployed-build.sh <api-base-url> <expected-sha>}"
ATTEMPTS="${ATTEMPTS:-20}"
SLEEP="${SLEEP:-6}"

echo "== build identity: $BASE =="
echo "   expecting $EXPECTED"

ACTUAL=""
for i in $(seq 1 "$ATTEMPTS"); do
  BODY=$(curl -s --max-time 10 "$BASE/health/build" || true)
  # Parsed, not grepped. A 404 page containing the sha somewhere would satisfy a
  # substring match, and a redirect to a login page would too.
  ACTUAL=$(printf '%s' "$BODY" | python3 -c '
import json, sys
try:
    print(json.load(sys.stdin).get("sha", ""))
except Exception:
    print("")
' 2>/dev/null || true)

  if [ "$ACTUAL" = "$EXPECTED" ]; then
    echo "   the deployed build is $ACTUAL, after $i attempt(s)"
    echo "== identity confirmed =="
    exit 0
  fi

  echo "   attempt $i: got '${ACTUAL:-no sha}'"
  sleep "$SLEEP"
done

echo "::error::The deployment is not running the build being released." >&2
echo "  expected: $EXPECTED" >&2
echo "  reported: ${ACTUAL:-nothing usable from $BASE/health/build}" >&2
echo "  An empty or missing sha means the running image predates /health/build, or was built" >&2
echo "  without BARAKO_BUILD_SHA. Either way this deploy has not been proven." >&2
exit 1

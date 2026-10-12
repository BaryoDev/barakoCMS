#!/usr/bin/env bash
#
# Fails when the revision document breaks a consumer of the base document, naming each break.
#
# oasdiff classifies the change. Two things are raised above its defaults, in
# openapi/severity-levels.txt:
#
#   - Removing a response property. This API marks no response property required, so oasdiff
#     files a removed or renamed one as an optional removal, at info level. The console reads those
#     fields, and CLAUDE.md section 6 counts removing or renaming one as breaking.
#   - Removing an enum value from a response, and removing something after it was deprecated. Both
#     are named as breaking in #631, and a deprecation notice is not a version move.
#
# --fail-on WARN, so a removed request property or a tightened pattern also fails. Additions are
# info level and pass: a gate that fires on every new optional field gets routed around.
#
# Usage: scripts/openapi-diff.sh <base document> <revision document>
# Exit: 0 no break, 1 break found (or oasdiff could not compare the documents).

set -uo pipefail

cd "$(dirname "$0")/.."

BASE="${1:?usage: scripts/openapi-diff.sh <base> <revision>}"
REVISION="${2:?usage: scripts/openapi-diff.sh <base> <revision>}"

command -v oasdiff >/dev/null || { echo "openapi-diff: oasdiff is not installed; run scripts/install-oasdiff.sh" >&2; exit 1; }
[ -s "$BASE" ] || { echo "openapi-diff: $BASE is missing or empty" >&2; exit 1; }
[ -s "$REVISION" ] || { echo "openapi-diff: $REVISION is missing or empty" >&2; exit 1; }

oasdiff breaking "$BASE" "$REVISION" \
    --severity-levels openapi/severity-levels.txt \
    --fail-on WARN
status=$?

case "$status" in
    0) echo "openapi-diff: no breaking change from $BASE to $REVISION" ;;
    1) echo "openapi-diff: breaking change from $BASE to $REVISION (listed above)" ;;
    *) echo "openapi-diff: oasdiff exited $status, so nothing was compared"; status=1 ;;
esac
exit "$status"

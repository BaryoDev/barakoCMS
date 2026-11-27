#!/usr/bin/env bash
#
# The checks a release makes on the OpenAPI document before and after it is attached (#1438).
#
#   scripts/openapi-release-check.sh <document> [baseline]
#
# Fails when the document is missing or empty, is not a JSON object, has no paths, or has no
# /api/public/{type}, the path every instance serves for public delivery. With a baseline, it also
# fails when the document is not byte for byte the baseline. The release job and the openapi job
# in ci.yml both pass openapi/baseline.json, so a pull request that changes the served API has to
# refresh the baseline in the same change, and a release cannot ship an API nobody reviewed.
# scripts/openapi-contract-check.sh is what judges whether a change breaks a consumer; this is the
# check that the baseline is current.
#
# The document is the one scripts/openapi-fetch.sh writes, which is already normalised (sorted keys,
# no "servers"), so a byte comparison is a fair one.
set -euo pipefail

DOC="${1:?usage: scripts/openapi-release-check.sh <document> [baseline]}"
BASELINE="${2:-}"

fail() { printf 'openapi-release-check: %s\n' "$1" >&2; exit 1; }

[ -f "$DOC" ] || fail "$DOC does not exist"
[ -s "$DOC" ] || fail "$DOC is empty"

python3 - "$DOC" <<'PY' || exit 1
import json, sys

try:
    doc = json.load(open(sys.argv[1], encoding="utf-8"))
except ValueError as e:
    sys.exit(f"openapi-release-check: {sys.argv[1]} is not JSON: {e}")
if not isinstance(doc, dict):
    sys.exit("openapi-release-check: the document is not a JSON object")
paths = doc.get("paths")
if not isinstance(paths, dict) or not paths:
    sys.exit("openapi-release-check: the document has no paths")
if "/api/public/{type}" not in paths:
    sys.exit("openapi-release-check: the document has no /api/public/{type}")
print(f"openapi-release-check: {len(paths)} paths, /api/public/{{type}} present", file=sys.stderr)
PY

if [ -n "$BASELINE" ]; then
  [ -s "$BASELINE" ] || fail "the baseline $BASELINE is missing or empty"
  if ! cmp -s "$DOC" "$BASELINE"; then
    diff -u "$BASELINE" "$DOC" | head -60 >&2 || true
    fail "the served document differs from $BASELINE. Refresh the baseline with: bash scripts/openapi-fetch.sh $BASELINE   then review the diff and commit it in the same pull request"
  fi
  echo "openapi-release-check: identical to $BASELINE" >&2
fi

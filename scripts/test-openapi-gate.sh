#!/usr/bin/env bash
#
# Proves scripts/openapi-diff.sh can fail, and fails for the right reason.
#
# A green contract check on a correct document is also what a broken differ produces. These cases
# run the gate over committed fixtures in scripts/testdata/openapi/: each known-bad document must be
# refused with the changed name in the output, and the additive one must pass. Passing the document
# the job just fetched as an argument also renames one response property in it, so the gate is shown
# to read the real document's references, not only the fixtures' small ones.
#
# Usage: scripts/test-openapi-gate.sh [fetched document]

set -uo pipefail

cd "$(dirname "$0")/.."

FIX=scripts/testdata/openapi
pass=0
fail=0
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# expect <description> <pass|fail> <base> <revision> [text the output must contain]
expect() {
    local what="$1" want="$2" base="$3" rev="$4" needle="${5:-}" out status
    out=$(bash scripts/openapi-diff.sh "$base" "$rev" 2>&1)
    status=$?
    if [ "$want" = pass ] && [ "$status" -eq 0 ]; then
        echo "  ok    $what (passed)"; pass=$((pass + 1)); return
    fi
    if [ "$want" = fail ] && [ "$status" -ne 0 ]; then
        if [ -z "$needle" ] || printf '%s\n' "$out" | grep -qF -- "$needle"; then
            echo "  ok    $what (refused, naming $needle)"; pass=$((pass + 1)); return
        fi
        echo "  FAIL  $what: refused, but the output does not name $needle, so it is not refused for this reason"
    else
        echo "  FAIL  $what: expected $want, exit was $status"
    fi
    printf '%s\n' "$out" | sed 's/^/        /'
    fail=$((fail + 1))
}

expect "an unchanged document"                  pass "$FIX/base.json" "$FIX/base.json"
expect "an added optional response property"    pass "$FIX/base.json" "$FIX/added-optional-field.json"
expect "a renamed response property"            fail "$FIX/base.json" "$FIX/renamed-response-field.json" '`items`'
expect "a removed response enum value"          fail "$FIX/base.json" "$FIX/removed-enum-value.json" 'Archived'
expect "a renamed path"                         fail "$FIX/base.json" "$FIX/renamed-path.json" '/api/contents/{id}/history'

if [ $# -gt 0 ]; then
    LIVE="$1"
    renamed=$(python3 - "$LIVE" "$TMP/live-renamed.json" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
schemas = doc["components"]["schemas"]
# The first schema a 200 response references directly, with a property to rename.
for path, ops in sorted(doc["paths"].items()):
    for method, op in sorted(ops.items()):
        ref = (op.get("responses", {}).get("200", {}).get("content", {})
                 .get("application/json", {}).get("schema", {}).get("$ref", ""))
        name = ref.rsplit("/", 1)[-1]
        props = schemas.get(name, {}).get("properties") or {}
        if props:
            prop = sorted(props)[0]
            props["renamedByTheGateTest"] = props.pop(prop)
            json.dump(doc, open(sys.argv[2], "w", encoding="utf-8"))
            print(prop)
            sys.exit(0)
sys.exit("no 200 response with a referenced object schema was found to rename a property in")
PY
    ) || { echo "  FAIL  could not plant a rename in $LIVE"; fail=$((fail + 1)); renamed=""; }
    expect "the fetched document against itself"   pass "$LIVE" "$LIVE"
    [ -n "$renamed" ] && expect "a renamed property in the fetched document" fail "$LIVE" "$TMP/live-renamed.json" "\`$renamed\`"
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]

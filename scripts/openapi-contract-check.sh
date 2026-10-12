#!/usr/bin/env bash
#
# The OpenAPI contract gate (#631): fails when the API the Suite serves breaks a consumer of the
# reviewed baseline in openapi/baseline.json, naming each break.
#
#   1. The gate is shown to fail on known-bad fixtures before it is trusted.
#   2. The Suite is booted and its document fetched (scripts/openapi-fetch.sh).
#   3. The gate is shown to fail on a rename planted in that document.
#   4. With a base ref: if this change edits the baseline, the edit is printed for the reviewer,
#      and an edit that is itself breaking must move a contract version in the same diff.
#   5. The served document is diffed against the baseline. A break fails. An addition passes, and
#      is listed so the baseline can be refreshed in a reviewed commit.
#
# The baseline is never written here. Accepting a change is running
#   bash scripts/openapi-fetch.sh openapi/baseline.json
# and committing the diff, so a break lands only where someone reads it.
#
# Usage: scripts/openapi-contract-check.sh [base ref, e.g. origin/release/5.0.0]
#   OPENAPI_OUT  directory for the fetched document (default: a temporary one)

set -uo pipefail

cd "$(dirname "$0")/.."

BASE_REF="${1:-}"
BASELINE=openapi/baseline.json
OUT_DIR="${OPENAPI_OUT:-$(mktemp -d)}"
mkdir -p "$OUT_DIR"
CURRENT="$OUT_DIR/current.json"
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; echo "::error::$1"; exit 1; }

[ -s "$BASELINE" ] || fail "$BASELINE is missing; write it with scripts/openapi-fetch.sh and commit it"

step "the gate refuses the known-bad fixtures"
bash scripts/test-openapi-gate.sh || fail "the OpenAPI gate passed a known-bad document, so it is not working"

step "fetching the document the Suite serves"
bash scripts/openapi-fetch.sh "$CURRENT" || fail "could not fetch the OpenAPI document from the Suite"

step "the gate refuses a rename planted in the fetched document"
bash scripts/test-openapi-gate.sh "$CURRENT" || fail "the OpenAPI gate passed a rename in the real document, so it is not working"

if [ -n "$BASE_REF" ]; then
    step "baseline changes in this diff, against $BASE_REF"
    merge_base=$(git merge-base "$BASE_REF" HEAD) || fail "cannot find a merge base with $BASE_REF"
    if git cat-file -e "$merge_base:$BASELINE" 2>/dev/null; then
        git show "$merge_base:$BASELINE" > "$OUT_DIR/base-baseline.json"
        if cmp -s "$OUT_DIR/base-baseline.json" "$BASELINE"; then
            echo "the baseline is unchanged"
        else
            {
                echo "## OpenAPI baseline changes this diff accepts"
                echo '```'
                oasdiff changelog "$OUT_DIR/base-baseline.json" "$BASELINE" 2>&1 || true
                echo '```'
            } | tee -a "$SUMMARY"
            if ! bash scripts/openapi-diff.sh "$OUT_DIR/base-baseline.json" "$BASELINE" >/dev/null 2>&1; then
                # CLAUDE.md section 6: a breaking HTTP change moves ApiContract.Version (or the
                # delivery number beside it), or the module's HttpContractVersion for a module route.
                # ApiContract's two numbers, a module's HttpContractVersion, or a module contract
                # constant it returns (Pages returns PagesContract.Version). ModuleContract is the
                # package contract, not an HTTP one.
                moved=$(git diff "$merge_base" -- '*.cs' ':(exclude)BarakoCMS.Abstractions/Modules/ModuleContract.cs' \
                    | grep -E '^[+-][^+-].*(HttpContractVersion[[:space:]]*=>|const int (Delivery)?Version[[:space:]]*=)' || true)
                [ -n "$moved" ] || fail "the baseline accepts a breaking change, but neither ApiContract.cs nor a module's HttpContractVersion moved (CLAUDE.md section 6)"
                echo "the baseline accepts a breaking change, and a contract version moved with it"
            fi
        fi
    else
        echo "$BASE_REF has no baseline yet, so there is nothing to compare it with"
    fi
fi

step "the served document against $BASELINE"
if ! bash scripts/openapi-diff.sh "$BASELINE" "$CURRENT" | tee "$OUT_DIR/breaking.txt"; then
    {
        echo "## OpenAPI contract: breaking change"
        echo '```'
        cat "$OUT_DIR/breaking.txt"
        echo '```'
        echo "If the break is intended, run \`bash scripts/openapi-fetch.sh $BASELINE\`, commit the diff,"
        echo "move the contract version (CLAUDE.md section 6) and name the break in the pull request."
    } >> "$SUMMARY"
    fail "the API breaks a consumer of $BASELINE (listed above)"
fi

oasdiff changelog "$BASELINE" "$CURRENT" > "$OUT_DIR/additions.txt" 2>&1 || true
if grep -qE '^[0-9]+ changes?:' "$OUT_DIR/additions.txt"; then
    echo "::notice::The API has additions the baseline does not list yet. Refresh it with scripts/openapi-fetch.sh $BASELINE in a reviewed commit."
    {
        echo "## OpenAPI contract: additions not yet in the baseline"
        echo '```'
        cat "$OUT_DIR/additions.txt"
        echo '```'
    } | tee -a "$SUMMARY"
fi

echo "OpenAPI contract: no breaking change against $BASELINE"

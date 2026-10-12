#!/usr/bin/env bash
#
# Proves scripts/perf-budget.py fails for the reasons it exists and passes an improvement, against
# the fixtures in scripts/testdata/perf-budget/ and the committed perf-budget.json.
#
#   bash scripts/test-perf-budget.sh

set -uo pipefail

cd "$(dirname "$0")/.."

FIX=scripts/testdata/perf-budget
pass=0
fail=0

expect() { # <description> <pass|fail> <results> [text the output must contain]...
    local what="$1" want="$2" results="$3" out status needle
    shift 3
    out=$(python3 scripts/perf-budget.py "$results" perf-budget.json 2>&1)
    status=$?
    if { [ "$want" = pass ] && [ "$status" -eq 0 ]; } || { [ "$want" = fail ] && [ "$status" -ne 0 ]; }; then
        for needle in "$@"; do
            if ! printf '%s\n' "$out" | grep -qF -- "$needle"; then
                echo "  FAIL  $what: $want as expected, but the output does not say '$needle'"
                printf '%s\n' "$out" | sed 's/^/        /'
                fail=$((fail + 1)); return
            fi
        done
        echo "  ok    $what ($want)"; pass=$((pass + 1)); return
    fi
    echo "  FAIL  $what: expected $want, exit was $status"
    printf '%s\n' "$out" | sed 's/^/        /'
    fail=$((fail + 1))
}

expect "every metric at its baseline"          pass "$FIX/within.json"
expect "an image over its size budget"         fail "$FIX/image-over-budget.json" \
    "image_compressed_mb is 180, over its budget of 127 (baseline 120.7)" "| image_compressed_mb | 120.7 | 180 | +59.3 | 127 | OVER BUDGET |"
expect "lower memory at rest"                  pass "$FIX/memory-improved.json" \
    "| memory_at_rest_mb | 233.7 | 180 | -53.7 | 280 | within, better than baseline |"
expect "a budgeted metric missing from the run" fail "$FIX/metric-missing.json" "startup_seconds has no numeric value"
expect "a measured metric with no budget line" fail "$FIX/metric-unbudgeted.json" "threads_at_rest was measured but has no line"
expect "an architecture with no budgets yet"   pass "$FIX/arm64-recorded.json" "recorded, no budget yet"
expect "an architecture the file does not name" fail "$FIX/unknown-arch.json" "no budgets for 'riscv64'"
expect "an unreadable results file"            fail "$FIX/unreadable.json" "cannot read"

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]

#!/usr/bin/env bash
#
# Proves scripts/check-method-length.py fails for each reason it exists, against throwaway trees.
# The braces inside the strings, characters and comments are there to show they are not counted.
#
#   bash scripts/test-method-length.sh

set -uo pipefail

cd "$(dirname "$0")/.."

CHECK="$PWD/scripts/check-method-length.py"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
pass=0
fail=0

# write_tree <dir> <lines in Long's body> [extra member]
write_tree() {
    local dir="$1" body="$2" extra="${3:-}"
    mkdir -p "$dir/barakoCMS"
    {
        echo 'namespace Fixture;'
        echo 'internal sealed class Sample'
        echo '{'
        echo '    public string Long(int x)'
        echo '    {'
        for _ in $(seq 1 "$body"); do
            echo '        var s = "}" + $"{x}}}" + @"{""" + '"'"'{'"'"' + """ } """; // }'
        done
        echo '        return "";'
        echo '    }'
        echo '    /* } } */'
        printf '%s\n' "$extra"
        echo '}'
    } > "$dir/barakoCMS/Sample.cs"
    # Enough short methods for the scan's own control.
    {
        echo 'namespace Fixture;'
        echo 'internal sealed class Short'
        echo '{'
        for n in $(seq 1 12); do echo "    public int M$n(int a) { if (a > 0) { return a; } return $n; }"; done
        echo '}'
    } > "$dir/barakoCMS/Short.cs"
}

expect() { # <description> <pass|fail> <dir> [text the output must contain]
    local what="$1" want="$2" dir="$3" needle="${4:-}" out status
    out=$(python3 "$CHECK" --root "$dir" --baseline "$dir/baseline.txt" 2>&1)
    status=$?
    if { [ "$want" = pass ] && [ "$status" -eq 0 ]; } || { [ "$want" = fail ] && [ "$status" -ne 0 ]; }; then
        if [ -z "$needle" ] || printf '%s\n' "$out" | grep -qF -- "$needle"; then
            echo "  ok    $what ($want)"; pass=$((pass + 1)); return
        fi
        echo "  FAIL  $what: $want as expected, but the output does not say '$needle'"
    else
        echo "  FAIL  $what: expected $want, exit was $status"
    fi
    printf '%s\n' "$out" | sed 's/^/        /'
    fail=$((fail + 1))
}

# Long's length is its body plus 4 lines: signature, two braces and the return.
write_tree "$TMP/a" 76
python3 "$CHECK" --root "$TMP/a" --baseline "$TMP/a/baseline.txt" --update >/dev/null
grep -q '^80 barakoCMS/Sample.cs::Sample.Long$' "$TMP/a/baseline.txt" \
    && { echo "  ok    the baseline records the method at its real length (80)"; pass=$((pass + 1)); } \
    || { echo "  FAIL  the baseline does not record Sample.Long at 80 lines:"; sed 's/^/        /' "$TMP/a/baseline.txt"; fail=$((fail + 1)); }
expect "an unchanged tree" pass "$TMP/a"

write_tree "$TMP/a" 77
expect "a listed method that grew by one line" fail "$TMP/a" "grew from 80 to 81 lines"

write_tree "$TMP/a" 70
expect "a listed method that shrank but is still over" pass "$TMP/a" "down from 80 to 74 lines"

write_tree "$TMP/a" 60
expect "a listed method now within the limit" fail "$TMP/a" "within 70 lines now"

write_tree "$TMP/a" 76 "$(printf '    public void Another()\n    {\n'; for _ in $(seq 1 70); do echo '        System.Console.WriteLine();'; done; printf '    }\n')"
expect "a new method over the limit" fail "$TMP/a" "Sample.Another is 73 lines, over the 70-line limit and not in the baseline"

rm "$TMP/a/barakoCMS/Sample.cs"
expect "a listed method that no longer exists" fail "$TMP/a" "no longer exists"

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]

#!/usr/bin/env bash
# Tests that release-notes.sh prints something GitHub will accept as a release body.
#
# GitHub refuses a body over 125,000 characters. 4.0.0's section was 196,028, and the release job
# found out after every package was published (#660). The fixture is small, so the limit is set
# small to match; the script applies the same logic at its real default.
#
#   bash scripts/test-release-notes.sh

set -uo pipefail

cd "$(dirname "$0")/.."
FIXTURE="scripts/testdata/release-notes/CHANGELOG.md"
LIMIT=1200

pass=0
fail=0

check() { # $1 = description, $2 = condition already evaluated as an exit code
  if [ "$2" = 0 ]; then
    echo "  ok    $1"
    pass=$((pass + 1))
  else
    echo "  FAIL  $1"
    fail=$((fail + 1))
  fi
}

chars() { LC_ALL=C.UTF-8 wc -m < "$1" | tr -d ' '; }

# One "### Name" subsection, heading included, up to the next heading.
subsection() { awk -v h="### $2" '$0 == h { f = 1; print; next } f && /^##/ { exit } f' "$1"; }

# One "## [version]" section's body, with the blank lines around it trimmed, as the script prints it.
section() { awk -v h="## [$2]" 'index($0, h) == 1 { f = 1; next } f && /^## / { exit } f' "$1" \
  | awk 'NF { s = 1 } s' | tac | awk 'NF { s = 1 } s' | tac; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

echo "== a section under the limit is printed exactly as written =="
RELEASE_NOTES_LIMIT=$LIMIT bash scripts/release-notes.sh 9.2.0 "$FIXTURE" > "$WORK/short.md" 2>/dev/null
check "it exits zero" $?
section "$FIXTURE" 9.2.0 > "$WORK/short-expected.md"
[ -s "$WORK/short-expected.md" ]; check "the expected section is not empty" $?
cmp -s "$WORK/short-expected.md" "$WORK/short.md"; check "the output is the section, byte for byte" $?

echo "== a section over the limit is summarised to fit =="
section "$FIXTURE" 9.1.0 > "$WORK/long-full.md"
[ "$(chars "$WORK/long-full.md")" -gt "$LIMIT" ]; check "the fixture section really is over the limit" $?
RELEASE_NOTES_LIMIT=$LIMIT bash scripts/release-notes.sh 9.1.0 "$FIXTURE" > "$WORK/long.md" 2>/dev/null
check "it exits zero" $?
[ -s "$WORK/long.md" ] && [ "$(chars "$WORK/long.md")" -le "$LIMIT" ]; check "the output is not empty and fits the limit" $?
for name in Breaking Security; do
  subsection "$WORK/long-full.md" "$name" > "$WORK/want-$name"
  subsection "$WORK/long.md" "$name" > "$WORK/got-$name"
  [ -s "$WORK/want-$name" ] && cmp -s "$WORK/want-$name" "$WORK/got-$name"; check "$name is printed in full" $?
done
grep -qF 'blob/v9.1.0/CHANGELOG.md#910---2026-01-02' "$WORK/long.md"; check "it links to the version's changelog anchor" $?
subsection "$WORK/long.md" Added | grep -q '^3 entries\. '; check "Added becomes a count of its 3 entries" $?
subsection "$WORK/long.md" Fixed | grep -q '^2 entries\. '; check "Fixed becomes a count of its 2 entries" $?
subsection "$WORK/long.md" Removed | grep -q '^1 entry\. '; check "Removed becomes a count of its 1 entry" $?
! grep -q 'Added entry one' "$WORK/long.md"; check "the summarised entries are not printed" $?
grep -q '^A release with more notes' "$WORK/long.md"; check "the intro above the first subsection is kept" $?

echo "== a section that cannot fit even summarised is refused =="
RELEASE_NOTES_LIMIT=$LIMIT bash scripts/release-notes.sh 9.0.0 "$FIXTURE" > "$WORK/huge.md" 2>/dev/null
[ $? -ne 0 ]; check "it exits non-zero, so the release gate stops before publishing" $?

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1

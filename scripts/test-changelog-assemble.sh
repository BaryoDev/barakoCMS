#!/usr/bin/env bash
# Tests where changelog-assemble.sh puts an entry.
#
# It used to find `### Fixed` with an unscoped search, so once a release had emptied Unreleased the
# first heading in the file belonged to the version that just shipped, and every fragment was filed
# under it. Nothing failed: the script printed "Assembled 5 into Fixed" and the entries were in the
# previous release's notes, where the next release body would never read them.
#
# Run against a fixture in a temp copy, so being wrong here is free.
#
#   bash scripts/test-changelog-assemble.sh

set -uo pipefail

cd "$(dirname "$0")/.."
ROOT=$(pwd)
FIXTURE="$ROOT/scripts/testdata/changelog-assemble"

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

# The line number of a release heading, so "above it" and "below it" can be compared.
line_of() { grep -n "$2" "$1" | head -1 | cut -d: -f1; }

setup() { # $1 = work dir
  mkdir -p "$1/fragments"
  cp "$FIXTURE/CHANGELOG.md" "$1/CHANGELOG.md"
  cp "$FIXTURE"/fragments/*.md "$1/fragments/"
}

echo "== an empty Unreleased gets the entries, not the last release =="
WORK=$(mktemp -d)
setup "$WORK"
CHANGELOG_FILE="$WORK/CHANGELOG.md" CHANGELOG_DIR="$WORK/fragments" bash scripts/changelog-assemble.sh >/dev/null
released=$(line_of "$WORK/CHANGELOG.md" '^## \[9\.1\.0\]')
new_fixed=$(line_of "$WORK/CHANGELOG.md" 'A new fixed entry')
new_security=$(line_of "$WORK/CHANGELOG.md" 'A new security entry')
[ -n "$new_fixed" ] && [ "$new_fixed" -lt "$released" ]; check "the new Fixed entry is above the released heading" $?
[ -n "$new_security" ] && [ "$new_security" -lt "$released" ]; check "the new Security entry is above the released heading" $?

# The released section has to come out byte for byte as it went in. Checking only that the new entry
# is above it would pass a script that wrote it in both places.
awk '/^## \[9\.1\.0\]/,0' "$WORK/CHANGELOG.md" > "$WORK/after.txt"
awk '/^## \[9\.1\.0\]/,0' "$FIXTURE/CHANGELOG.md" > "$WORK/before.txt"
diff -q "$WORK/before.txt" "$WORK/after.txt" >/dev/null; check "the released section is untouched" $?

echo "== a second run appends under the heading the first run created =="
cp "$FIXTURE"/fragments/101.Fixed.md "$WORK/fragments/"
sed -i 's/A new fixed entry/A later fixed entry/' "$WORK/fragments/101.Fixed.md"
CHANGELOG_FILE="$WORK/CHANGELOG.md" CHANGELOG_DIR="$WORK/fragments" bash scripts/changelog-assemble.sh >/dev/null
first=$(line_of "$WORK/CHANGELOG.md" 'A new fixed entry')
later=$(line_of "$WORK/CHANGELOG.md" 'A later fixed entry')
released=$(line_of "$WORK/CHANGELOG.md" '^## \[9\.1\.0\]')
[ -n "$later" ] && [ "$later" -gt "$first" ] && [ "$later" -lt "$released" ]; check "it lands after the existing entry and still above the release" $?
[ "$(grep -c '^### Fixed' "$WORK/CHANGELOG.md")" = 2 ]; check "no duplicate Fixed heading was created" $?

echo "== a changelog with no Unreleased heading is refused =="
WORK2=$(mktemp -d)
setup "$WORK2"
grep -v '^## \[Unreleased\]' "$FIXTURE/CHANGELOG.md" > "$WORK2/CHANGELOG.md"
CHANGELOG_FILE="$WORK2/CHANGELOG.md" CHANGELOG_DIR="$WORK2/fragments" bash scripts/changelog-assemble.sh >/dev/null 2>&1
[ $? -ne 0 ]; check "it exits non-zero rather than guessing a section" $?

rm -rf "$WORK" "$WORK2"
echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1

#!/usr/bin/env bash
# Refuses a tree containing git conflict markers.
#
# A CHANGELOG with markers in it reached master, because resolving the same file on eight branches
# was scripted and the script's output was never checked. Nothing else looks: the markers were in
# Markdown, so no compiler, linter or test had an opinion, and every gate stayed green.
#
# Markdown and YAML are the dangerous cases precisely because nothing else parses them.
set -uo pipefail

cd "$(dirname "$0")/.."

# Anchored to the line start: "=======" also underlines a Markdown heading, and a diff of a diff can
# legitimately contain the others mid-line.
pattern='^(<<<<<<< |>>>>>>> |=======$)'

# Tracked files only, so a stray file in a working copy is not a CI failure. -I skips binaries.
hits=$(git grep -InE "$pattern" -- \
    ':!*.patch' ':!*.diff' ':!scripts/check-conflict-markers.sh' 2>/dev/null || true)

if [ -n "$hits" ]; then
    echo "Conflict markers in tracked files:"
    echo ""
    echo "$hits"
    echo ""
    echo "A merge or rebase was resolved without checking the result. Fix the file, do not delete"
    echo "the marker lines alone: one side of the conflict is usually missing too."
    exit 1
fi

# The same class of damage, from the same cause. Resolving CHANGELOG.md on eight branches by taking
# one side and pasting the other back duplicated entries instead of losing them, which no compiler
# reads either. A repeated bold lead is the signal: every entry opens with a distinct one.
dupes=$(grep '^- \*\*' CHANGELOG.md 2>/dev/null | sort | uniq -d || true)

if [ -n "$dupes" ]; then
    echo "Duplicate CHANGELOG entries:"
    echo ""
    echo "$dupes" | cut -c1-100
    echo ""
    echo "An entry was pasted back into a file that already held it. Keep one."
    exit 1
fi

echo "No conflict markers, no duplicate changelog entries."

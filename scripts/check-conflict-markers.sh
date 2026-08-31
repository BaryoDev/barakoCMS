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

echo "No conflict markers."

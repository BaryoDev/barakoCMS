#!/usr/bin/env bash
# Prints one version's section of CHANGELOG.md, for use as a GitHub Release body.
#
#   scripts/release-notes.sh 3.21.0            # reads CHANGELOG.md
#   scripts/release-notes.sh 3.21.0 other.md
#
# Exits non-zero when there is no section for that version, or when the section is
# empty. That is why this is a script and not an inline sed in the release job: the
# job creates a GitHub Release from this output, and a release note that is blank
# because nobody wrote the heading is the failure worth catching. It fails before
# the tag exists rather than after.
set -euo pipefail

VERSION="${1:?usage: release-notes.sh <version> [changelog]}"
CHANGELOG="${2:-CHANGELOG.md}"

[ -f "$CHANGELOG" ] || { echo "release-notes: $CHANGELOG not found" >&2; exit 1; }

# Matched with index(), not a regex. The dots in a version are wildcards to awk, so
# an unescaped 3.21.0 also matches a heading for 3x21y0, and escaping the version
# into a regex has to survive both the shell and awk's own handling of -v escapes.
# A literal prefix match has neither problem.
BODY=$(awk -v head="## [$VERSION]" '
  !inside && index($0, head) == 1 { inside = 1; next }
  inside && /^## / { exit }
  inside { print }
' "$CHANGELOG")

# Drop leading and trailing blank lines. A section that is only blank lines has to
# read as absent, not as a body made of whitespace.
BODY=$(printf '%s\n' "$BODY" | awk 'NF { seen = 1 } seen { print }' | awk '
  { lines[NR] = $0 }
  END {
    last = 0
    for (i = 1; i <= NR; i++) if (lines[i] ~ /[^[:space:]]/) last = i
    for (i = 1; i <= last; i++) print lines[i]
  }')

if [ -z "$BODY" ]; then
  echo "release-notes: $CHANGELOG has no non-empty '## [$VERSION]' section." >&2
  echo "release-notes: write the section before releasing $VERSION." >&2
  exit 1
fi

printf '%s\n' "$BODY"

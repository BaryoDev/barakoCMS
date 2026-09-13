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
#
# GitHub refuses a release body over 125,000 characters. 4.0.0's section was 196,028,
# and the release job found out after every package was published (#660). So a
# section over the limit is summarised: Breaking and Security stay whole, since those
# are what a reader has to act on, and every other subsection becomes a count and a
# link to CHANGELOG.md. If even that does not fit, the script exits non-zero, and the
# release workflow runs it in the gate job, before anything is published.
#
# RELEASE_NOTES_LIMIT overrides the limit (default 120000, leaving a margin under
# GitHub's 125,000). A section that fits is printed exactly as before.
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

LIMIT="${RELEASE_NOTES_LIMIT:-120000}"
REPO_URL="${RELEASE_NOTES_REPO_URL:-https://github.com/BaryoDev/barakoCMS}"

# GitHub counts characters, not bytes, and the changelog is not all ASCII.
chars() { printf '%s\n' "$1" | LC_ALL=C.UTF-8 wc -m | tr -d ' '; }

LENGTH=$(chars "$BODY")
if [ "$LENGTH" -le "$LIMIT" ]; then
  printf '%s\n' "$BODY"
  exit 0
fi

# The anchor GitHub generates for "## [4.0.0] - 2026-09-07" is "400---2026-09-07".
HEADING=$(awk -v head="## [$VERSION]" 'index($0, head) == 1 { print; exit }' "$CHANGELOG")
ANCHOR=$(printf '%s\n' "${HEADING#\#\# }" | awk '{
  s = tolower($0); gsub(/[^a-z0-9 _-]/, "", s); gsub(/ /, "-", s); print s }')
LINK="$REPO_URL/blob/v$VERSION/CHANGELOG.md#$ANCHOR"

SUMMARY=$(printf '%s\n' "$BODY" | awk -v link="$LINK" '
  function flush() {
    if (name == "") return
    if (name == "Breaking" || name == "Security") { out = out text; return }
    out = out sprintf("### %s\n\n%d %s. Read %s in [CHANGELOG.md](%s).\n\n", name, count,
      count == 1 ? "entry" : "entries", count == 1 ? "it" : "them", link)
  }
  /^### / { flush(); name = substr($0, 5); sub(/[[:space:]]+$/, "", name); text = ""; count = 0 }
  name == "" { intro = intro $0 "\n"; next }
  { text = text $0 "\n"; if (/^- /) count++ }
  END {
    printf "%s", intro
    printf "These notes are longer than a GitHub release can hold, so only Breaking and Security are printed in full. The complete notes are in [CHANGELOG.md](%s).\n\n", link
    flush()
    printf "%s", out
  }' | awk 'NF { seen = 1 } seen { print }' | awk '
  { lines[NR] = $0 }
  END {
    last = 0
    for (i = 1; i <= NR; i++) if (lines[i] ~ /[^[:space:]]/) last = i
    for (i = 1; i <= last; i++) print lines[i]
  }')

SUMMARY_LENGTH=$(chars "$SUMMARY")
if [ "$SUMMARY_LENGTH" -gt "$LIMIT" ]; then
  echo "release-notes: the $VERSION section is $LENGTH characters, and even with only Breaking and Security in full it is $SUMMARY_LENGTH, over the $LIMIT limit." >&2
  echo "release-notes: shorten those sections in $CHANGELOG before releasing $VERSION." >&2
  exit 1
fi

echo "release-notes: the $VERSION section is $LENGTH characters, over $LIMIT; printed a $SUMMARY_LENGTH character summary." >&2
printf '%s\n' "$SUMMARY"

#!/usr/bin/env bash
# Folds changelog.d/ fragments into CHANGELOG.md's Unreleased section.
#
# One file per change means two branches adding two entries do not touch the same file, which is the
# point. Eight pull requests all editing CHANGELOG.md is what produced conflict markers on master
# (#390) and three duplicated entries (#391), both of which passed every other gate because nothing
# reads Markdown. See #392.
#
#   assemble           move the fragments into CHANGELOG.md and delete them
#   assemble --check   validate the fragments and change nothing
set -euo pipefail

cd "$(dirname "$0")/.."

DIR=changelog.d
FILE=CHANGELOG.md
CHECK=0
[ "${1:-}" = "--check" ] && CHECK=1

# The headings CHANGELOG.md already uses, in the order it uses them.
SECTIONS="Breaking Added Changed Removed Fixed Security"

# Written as an escape rather than typed, because a literal one in this file is the thing it bans.
EM_DASH=$(printf '\xe2\x80\x94')

fail() { echo "$1" >&2; exit 1; }

shopt -s nullglob
fragments=("$DIR"/*.md)
shopt -u nullglob

# Filenames carry the section, so a fragment cannot land under a heading nobody chose.
valid=0
# ${a[@]} on an empty array trips set -u on bash 3.2, which is what macOS ships.
for f in ${fragments[@]+"${fragments[@]}"}; do
    base=$(basename "$f")
    [ "$base" = "README.md" ] && continue

    section=$(echo "$base" | awk -F. '{print $(NF-1)}')
    case " $SECTIONS " in
        *" $section "*) ;;
        *) fail "$f: '$section' is not a section. Name it <slug>.<section>.md, section one of: $SECTIONS" ;;
    esac

    [ -s "$f" ] || fail "$f is empty"
    head -1 "$f" | grep -q '^- \*\*' \
        || fail "$f must open with a bolded lead, like '- **What was wrong.** What changed.'"
    if grep -q "$EM_DASH" "$f"; then
        fail "$f contains an em dash"
    fi

    valid=$((valid + 1))
done

if [ "$CHECK" = 1 ]; then
    echo "$valid fragment(s), all well formed."
    exit 0
fi

if [ "$valid" -eq 0 ]; then
    echo "No fragments to assemble."
    exit 0
fi

for section in $SECTIONS; do
    shopt -s nullglob
    matching=("$DIR"/*."$section".md)
    shopt -u nullglob
    [ ${#matching[@]} -eq 0 ] && continue

    body=$(cat ${matching[@]+"${matching[@]}"})

    # Inserted at the end of the section, before the next heading, so ordering within a release
    # follows the order things were merged rather than the order of a directory listing.
    python3 - "$FILE" "$section" "$body" <<'PY'
import sys
path, section, body = sys.argv[1], sys.argv[2], sys.argv[3]
s = open(path, encoding='utf-8').read()
heading = f"\n### {section}\n"
if heading not in s:
    raise SystemExit(f"{path} has no '### {section}' heading in the unreleased section")
start = s.index(heading) + len(heading)
candidates = [x for x in (s.find("\n### ", start), s.find("\n## ", start)) if x != -1]
cut = min(candidates) if candidates else len(s)
s = s[:cut].rstrip('\n') + '\n\n' + body.rstrip('\n') + '\n' + s[cut:]
open(path, 'w', encoding='utf-8').write(s)
PY

    rm -f ${matching[@]+"${matching[@]}"}
    echo "Assembled ${#matching[@]} into $section"
done

echo "Done. Review the diff before committing."

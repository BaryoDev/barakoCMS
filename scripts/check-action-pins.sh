#!/usr/bin/env bash
# Fails when a workflow step uses an action that is not pinned by a full 40-character commit SHA.
# A tag or branch can be moved after review; a SHA cannot.
#
#   bash scripts/check-action-pins.sh [<workflows-dir>]    default .github/workflows
#
# Composite actions under the sibling actions/ folder (action.yml and action.yaml at any depth under .github/actions) are
# checked the same way when it exists. Local actions (./path) are exempt. docker:// references must carry a sha256 digest.
set -uo pipefail

dir=${1:-.github/workflows}
bad=0
checked=0

actions_dir="$(dirname "$dir")/actions"
files=()
shopt -s nullglob
for f in "$dir"/*.yml "$dir"/*.yaml; do files+=("$f"); done
if [ -d "$actions_dir" ]; then
    while IFS= read -r f; do files+=("$f"); done < <(find "$actions_dir" -type f \( -name action.yml -o -name action.yaml \) | sort)
fi

# "uses" as a key: block (- uses: x), quoted ("uses": x) or inside a flow map (- {name: a, uses: x}).
# Text elsewhere in a line, such as a script that prints a workflow, is not a key.
q='["'"'"']?'
key_re="(^[[:space:]-]*|[{,][[:space:]]*)${q}uses${q}[[:space:]]*:[[:space:]]*(.*)\$"

for f in "${files[@]}"; do
    while IFS= read -r line; do
        [[ "$line" =~ ^[[:space:]]*# ]] && continue
        [[ "$line" =~ $key_re ]] || continue
        ref=${BASH_REMATCH[2]}
        if [[ "$ref" =~ ^\"([^\"]*)\" ]]; then
            ref=${BASH_REMATCH[1]}
        elif [[ "$ref" =~ ^\'([^\']*)\' ]]; then
            ref=${BASH_REMATCH[1]}
        else
            ref=$(printf '%s' "$ref" | sed -E 's/[[:space:]]*(#.*|[,}].*)?$//')
        fi
        case "$ref" in ./*) continue ;; esac
        checked=$((checked + 1))
        if [[ "$ref" == docker://* ]]; then
            [[ "$ref" =~ @sha256:[0-9a-f]{64}$ ]] && continue
        elif [[ "$ref" =~ @[0-9a-f]{40}$ ]]; then
            continue
        fi
        echo "::error file=$f::not pinned by a full commit SHA: $ref"
        bad=$((bad + 1))
    done < "$f"
done

echo "action pins: $checked checked, $bad not pinned"
[ "$checked" -gt 0 ] && [ "$bad" -eq 0 ]

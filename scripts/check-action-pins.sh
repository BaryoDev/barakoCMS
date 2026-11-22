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

check_ref() {
    local ref=$1 f=$2
    case "$ref" in ./*) return ;; esac
    checked=$((checked + 1))
    if [[ "$ref" == docker://* ]]; then
        [[ "$ref" =~ @sha256:[0-9a-f]{64}$ ]] && return
    elif [[ "$ref" =~ @[0-9a-f]{40}$ ]]; then
        return
    fi
    echo "::error file=$f::not pinned by a full commit SHA: $ref"
    bad=$((bad + 1))
}

for f in "${files[@]}"; do
    while IFS= read -r line; do
        [[ "$line" =~ ^[[:space:]]*# ]] && continue
        # A flow sequence can hold several uses keys on one line, so match again on what follows
        # each value until none is left.
        rest=$line
        while [[ "$rest" =~ $key_re ]]; do
            val=${BASH_REMATCH[2]}
            if [[ "$val" =~ ^\"([^\"]*)\" ]]; then
                ref=${BASH_REMATCH[1]}
                rest=${val:${#BASH_REMATCH[0]}}
            elif [[ "$val" =~ ^\'([^\']*)\' ]]; then
                ref=${BASH_REMATCH[1]}
                rest=${val:${#BASH_REMATCH[0]}}
            else
                [[ "$val" =~ ^([^#,}]*)(.*)$ ]]
                ref=${BASH_REMATCH[1]}
                rest=${BASH_REMATCH[2]}
                # A comment ends the line; a , or } leaves the next flow entry to match.
                [[ "$rest" == "#"* ]] && rest=""
                ref=${ref%"${ref##*[![:space:]]}"}
            fi
            check_ref "$ref" "$f"
        done
    done < "$f"
done

echo "action pins: $checked checked, $bad not pinned"
[ "$checked" -gt 0 ] && [ "$bad" -eq 0 ]

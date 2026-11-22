#!/usr/bin/env bash
# Fails when a workflow step uses an action that is not pinned by a full 40-character commit SHA.
# A tag or branch can be moved after review; a SHA cannot.
#
#   bash scripts/check-action-pins.sh [<workflows-dir>]    default .github/workflows
#
# Composite actions under the sibling actions/ folder (.github/actions/*/action.yml and .yaml) are
# checked the same way when it exists. Local actions (./path) are exempt. docker:// references must carry a sha256 digest.
set -uo pipefail

dir=${1:-.github/workflows}
bad=0
checked=0

shopt -s nullglob
for f in "$dir"/*.yml "$dir"/*.yaml "$(dirname "$dir")"/actions/*/action.yml "$(dirname "$dir")"/actions/*/action.yaml; do
    while IFS= read -r line; do
        ref=$(printf '%s' "$line" | sed -E 's/^[[:space:]-]*uses:[[:space:]]*//; s/[[:space:]]+#.*$//; s/[[:space:]]+$//; s/^"//; s/"$//; s/^'"'"'//; s/'"'"'$//')
        case "$ref" in ./*) continue ;; esac
        checked=$((checked + 1))
        if [[ "$ref" == docker://* ]]; then
            [[ "$ref" =~ @sha256:[0-9a-f]{64}$ ]] && continue
        elif [[ "$ref" =~ @[0-9a-f]{40}$ ]]; then
            continue
        fi
        echo "::error file=$f::not pinned by a full commit SHA: $ref"
        bad=$((bad + 1))
    done < <(grep -E '^[[:space:]-]*uses:[[:space:]]' "$f")
done

echo "action pins: $checked checked, $bad not pinned"
[ "$checked" -gt 0 ] && [ "$bad" -eq 0 ]

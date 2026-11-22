#!/usr/bin/env bash
# Prints the third-party images the compose files and manifests name, one per line, without
# BarakoCMS's own images (those are built and scanned separately) and without references that hold
# a variable. A reference with a digest is printed as repo@digest: the digest is what runs.
#
#   bash scripts/list-compose-images.sh [<repo-root>]
set -euo pipefail

root=${1:-.}

files=()
for f in "$root"/docker-compose*.yml "$root"/quickstart/docker-compose.yml; do
    [ -f "$f" ] && files+=("$f")
done
while IFS= read -r f; do files+=("$f"); done < <(find "$root/k8s" -name '*.yaml' 2>/dev/null | sort)

[ "${#files[@]}" -gt 0 ] || { echo "::error::no compose files or manifests under $root" >&2; exit 1; }

grep -hE '^[[:space:]-]*image:[[:space:]]' "${files[@]}" \
    | sed -E 's/^[[:space:]-]*image:[[:space:]]*//; s/[[:space:]]+#.*$//; s/^"//; s/"$//; s/^'"'"'//; s/'"'"'$//' \
    | grep -v '\$' \
    | grep -viE '^ghcr\.io/baryodev/' \
    | sed -E 's/^([^@]+):[^:@/]+@(sha256:.*)$/\1@\2/' \
    | sort -u

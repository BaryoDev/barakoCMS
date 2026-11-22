#!/usr/bin/env bash
# Prints the third-party images the compose files and manifests name, one per line, without
# BarakoCMS's own images (those are built and scanned separately). A reference with a digest is
# printed as repo@digest: the digest is what runs.
#
#   bash scripts/list-compose-images.sh [<repo-root>]
#
# A ${NAME:-default} or ${NAME-default} in a reference is replaced by its default, so
# `postgres:${PG_VERSION:-16}` is scanned as postgres:16. A variable with no default in a
# third-party reference cannot be scanned and must not be skipped without a word: the script names
# the file and the image on stderr and exits 1 after listing the rest.
#
# The files it reads, which CI also uses to decide whether a change touches a pinned image:
#   docker-compose*.yml, docker-compose*.yaml and quickstart/docker-compose*.yml|yaml,
#   and every .yml and .yaml under k8s/.
set -euo pipefail

root=${1:-.}

files=()
for f in "$root"/docker-compose*.yml "$root"/docker-compose*.yaml \
         "$root"/quickstart/docker-compose*.yml "$root"/quickstart/docker-compose*.yaml; do
    [ -f "$f" ] && files+=("$f")
done
while IFS= read -r f; do files+=("$f"); done < <(find "$root/k8s" \( -name '*.yaml' -o -name '*.yml' \) 2>/dev/null | sort)

[ "${#files[@]}" -gt 0 ] || { echo "::error::no compose files or manifests under $root" >&2; exit 1; }

unresolved=0
out=""
for f in "${files[@]}"; do
    refs=$(grep -hE '^[[:space:]-]*image:[[:space:]]' "$f" \
        | sed -E 's/^[[:space:]-]*image:[[:space:]]*//; s/[[:space:]]+#.*$//; s/^"//; s/"$//; s/^'"'"'//; s/'"'"'$//' \
        | sed -E 's/\$\{[A-Za-z_][A-Za-z0-9_]*:?-([^}]*)\}/\1/g' || true)
    while IFS= read -r ref; do
        [ -n "$ref" ] || continue
        case "$(printf '%s' "$ref" | tr '[:upper:]' '[:lower:]')" in ghcr.io/baryodev/*) continue ;; esac
        case "$ref" in
            *'$'*)
                echo "::error file=$f::image '$ref' holds a variable with no default, so it cannot be scanned; give it a default or pin it" >&2
                unresolved=$((unresolved + 1))
                continue
                ;;
        esac
        out+="$ref"$'\n'
    done <<<"$refs"
done

printf '%s' "$out" \
    | sed -E 's/^([^@]+):[^:@/]+@(sha256:.*)$/\1@\2/' \
    | sort -u

[ "$unresolved" -eq 0 ]

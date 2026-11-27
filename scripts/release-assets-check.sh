#!/usr/bin/env bash
#
# Checks the files a release attaches besides the packages: the production bundle, its sum and the
# OpenAPI document (#1436, #1438).
#
#   scripts/release-assets-check.sh <version> <directory> [--download <tag>]
#
# With --download, the three assets are read back from the GitHub release <tag> into the
# directory (which must be empty), and a missing one fails by name. Without it the directory is
# taken as it is, which is how the release job's build step and a local run use it.
#
# Then, on the files themselves: the sum matches the archive, the archive holds the six files the
# bundle is meant to carry (by name), its compose file defaults BARAKO_TAG to <version> and its
# .env.prod.example sets it and the compose project name, and the OpenAPI document has paths and
# /api/public/{type}.
#
# This does not check the compose file's mounts. scripts/build-prod-bundle.sh checks the short form
# the file uses today (an unquoted `- ./x:/y` line) when it builds, and scripts/prod-bundle-check.sh,
# run by the compose job in ci.yml, resolves every mount form against an unpacked copy.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:?usage: scripts/release-assets-check.sh <version> <directory> [--download <tag>]}"
DIR="${2:?usage: scripts/release-assets-check.sh <version> <directory> [--download <tag>]}"
TAG=""
case $# in
  2) ;;
  4) [ "$3" = "--download" ] || { echo "release-assets-check: unknown argument '$3'; only --download <tag> is accepted" >&2; exit 2; }
     TAG="$4" ;;
  *) echo "usage: scripts/release-assets-check.sh <version> <directory> [--download <tag>]" >&2; exit 2 ;;
esac

BUNDLE="barako-cms-prod-$VERSION.tar.gz"
SUMS="SHA256SUMS"
DOC="openapi-$VERSION.json"

fail() { printf 'release-assets-check: %s\n' "$1" >&2; exit 1; }

mkdir -p "$DIR"
DIR="$(cd "$DIR" && pwd)"

if [ -n "$TAG" ]; then
  [ -z "$(ls -A "$DIR")" ] || fail "$DIR is not empty, so what is read back could be something else"
  names="$(gh release view "$TAG" --json assets --jq '.assets[].name')" || fail "cannot read the release $TAG"
  for a in "$BUNDLE" "$SUMS" "$DOC"; do
    printf '%s\n' "$names" | grep -xF "$a" >/dev/null || fail "the release $TAG has no asset named $a"
  done
  gh release download "$TAG" --dir "$DIR" --pattern "$BUNDLE" --pattern "$SUMS" --pattern "$DOC" \
    || fail "cannot download the assets of $TAG"
fi

for a in "$BUNDLE" "$SUMS" "$DOC"; do
  [ -s "$DIR/$a" ] || fail "$a is missing or empty in $DIR"
done

if command -v sha256sum >/dev/null 2>&1; then
  (cd "$DIR" && sha256sum -c "$SUMS") || fail "the archive does not match $SUMS"
else
  (cd "$DIR" && shasum -a 256 -c "$SUMS") || fail "the archive does not match $SUMS"
fi

members="$(tar -tzf "$DIR/$BUNDLE")"
for f in docker-compose.prod.yml docker-compose.secrets.yml Caddyfile .env.prod.example scripts/backup-cron.sh scripts/db-app-role.sql; do
  printf '%s\n' "$members" | grep -xF "barako-cms-prod-$VERSION/$f" >/dev/null || fail "the archive has no $f"
done
tar -xzOf "$DIR/$BUNDLE" "barako-cms-prod-$VERSION/docker-compose.prod.yml" \
  | grep -xF "    image: ghcr.io/baryodev/barako-cms:\${BARAKO_TAG:-$VERSION}" >/dev/null \
  || fail "the bundle's compose file does not have the line image: ghcr.io/baryodev/barako-cms:\${BARAKO_TAG:-$VERSION}"
envfile="$(tar -xzOf "$DIR/$BUNDLE" "barako-cms-prod-$VERSION/.env.prod.example")"
printf '%s\n' "$envfile" | grep -xF "BARAKO_TAG=$VERSION" >/dev/null \
  || fail "the bundle's .env.prod.example does not set BARAKO_TAG=$VERSION"
printf '%s\n' "$envfile" | grep -xF "COMPOSE_PROJECT_NAME=barako-cms" >/dev/null \
  || fail "the bundle's .env.prod.example does not set COMPOSE_PROJECT_NAME=barako-cms"

bash scripts/openapi-release-check.sh "$DIR/$DOC"

echo "release-assets-check: $BUNDLE, $SUMS and $DOC are in order" >&2

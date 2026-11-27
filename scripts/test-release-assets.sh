#!/usr/bin/env bash
#
# Proves the release asset scripts refuse what they are meant to refuse (#1436, #1438).
# No containers, no network, no build.
#
#   bash scripts/test-release-assets.sh
#
# build-prod-bundle.sh runs from a throwaway copy of the files it reads, so a missing mount can be
# planted without touching the repository.
set -uo pipefail

cd "$(dirname "$0")/.."
REPO="$(pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

pass=0
fail=0
check() { # description, exit code of the thing being asserted
  if [ "$2" = 0 ]; then echo "  ok    $1"; pass=$((pass + 1)); else echo "  FAIL  $1"; fail=$((fail + 1)); fi
}
runs() { "$@" >/dev/null 2>&1; }
refuses() { ! "$@" >/dev/null 2>&1; }

echo "OpenAPI release check"
GOOD="$WORK/good.json"
printf '{"openapi":"3.0.0","paths":{"/api/public/{type}":{},"/api/schemas":{}}}\n' > "$GOOD"
runs bash scripts/openapi-release-check.sh "$GOOD"; check "a document with paths and the delivery path passes" $?
runs bash scripts/openapi-release-check.sh "$GOOD" "$GOOD"; check "a document identical to the baseline passes" $?
: > "$WORK/empty.json"
refuses bash scripts/openapi-release-check.sh "$WORK/empty.json"; check "an empty file fails" $?
refuses bash scripts/openapi-release-check.sh "$WORK/missing.json"; check "a missing file fails" $?
echo '{"paths":{}}' > "$WORK/nopaths.json"
refuses bash scripts/openapi-release-check.sh "$WORK/nopaths.json"; check "a document with no paths fails" $?
echo '{"openapi":"3.0.0"}' > "$WORK/nokey.json"
refuses bash scripts/openapi-release-check.sh "$WORK/nokey.json"; check "a document with no paths key fails" $?
echo '[1,2]' > "$WORK/array.json"
refuses bash scripts/openapi-release-check.sh "$WORK/array.json"; check "a JSON array fails" $?
echo 'not json' > "$WORK/text.json"
refuses bash scripts/openapi-release-check.sh "$WORK/text.json"; check "text that is not JSON fails" $?
echo '{"paths":{"/api/schemas":{}}}' > "$WORK/nopublic.json"
refuses bash scripts/openapi-release-check.sh "$WORK/nopublic.json"; check "a document without /api/public/{type} fails" $?
printf '{"openapi":"3.0.0","paths":{"/api/public/{type}":{},"/api/schemas":{},"/api/new":{}}}\n' > "$WORK/changed.json"
refuses bash scripts/openapi-release-check.sh "$WORK/changed.json" "$GOOD"; check "a document that differs from the baseline fails" $?
# An addition the baseline does not list is the case a pull request produces. It must fail, and the
# message must name the command that refreshes the baseline.
msg="$(bash scripts/openapi-release-check.sh "$WORK/changed.json" "$GOOD" 2>&1 || true)"
printf '%s' "$msg" | grep -qF 'bash scripts/openapi-fetch.sh'; check "an addition missing from the baseline fails, naming the refresh command" $?
printf '%s' "$msg" | grep -qF '/api/new'; check "the failure shows the added path" $?

echo "Production bundle"
SRC="$WORK/src"
mkdir -p "$SRC/scripts" "$WORK/out"
for f in scripts/build-prod-bundle.sh docker-compose.prod.yml docker-compose.secrets.yml Caddyfile .env.prod.example scripts/backup-cron.sh scripts/db-app-role.sql; do
  cp "$f" "$SRC/$f"
done
runs bash "$SRC/scripts/build-prod-bundle.sh" 9.8.7 "$WORK/out"; check "the bundle builds" $?
[ -s "$WORK/out/barako-cms-prod-9.8.7.tar.gz" ] && [ -s "$WORK/out/SHA256SUMS" ]; check "the archive and SHA256SUMS exist" $?
refuses bash scripts/release-assets-check.sh 9.8.7 "$WORK/out"; check "the read-back refuses a directory with no OpenAPI document" $?
cp "$GOOD" "$WORK/out/openapi-9.8.7.json"
runs bash scripts/release-assets-check.sh 9.8.7 "$WORK/out"; check "the read-back passes bundle, sum and document" $?
mkdir "$WORK/out2"
runs bash "$SRC/scripts/build-prod-bundle.sh" 9.8.7 "$WORK/out2"
cmp -s "$WORK/out/SHA256SUMS" "$WORK/out2/SHA256SUMS"; check "building twice gives the same sum" $?
tar -tzf "$WORK/out/barako-cms-prod-9.8.7.tar.gz" | grep -qx 'barako-cms-prod-9.8.7/scripts/backup-cron.sh'; check "the archive carries the backup script" $?
tar -xzOf "$WORK/out/barako-cms-prod-9.8.7.tar.gz" barako-cms-prod-9.8.7/docker-compose.prod.yml | grep -qF '${BARAKO_TAG:-9.8.7}'; check "the compose file defaults BARAKO_TAG to the version" $?
grep -qF '${BARAKO_TAG:?' "$REPO/docker-compose.prod.yml"; check "the repository's compose file still requires BARAKO_TAG" $?
bundled_compose="$(tar -xzOf "$WORK/out/barako-cms-prod-9.8.7.tar.gz" barako-cms-prod-9.8.7/docker-compose.prod.yml)"
bundled_env="$(tar -xzOf "$WORK/out/barako-cms-prod-9.8.7.tar.gz" barako-cms-prod-9.8.7/.env.prod.example)"
! printf '%s' "$bundled_compose" | grep -qF 'BARAKO_TAG is required'; check "the bundle's compose file does not say BARAKO_TAG is required" $?
printf '%s' "$bundled_compose" | grep -qF 'defaults to the release this bundle came from'; check "the bundle's compose file says the tag defaults to its release" $?
! printf '%s' "$bundled_env" | grep -qF 'Pin it'; check "the bundle's env example does not tell people to pin a tag it defaults" $?
printf '%s' "$bundled_env" | grep -qF 'defaults to the release it came from'; check "the bundle's env example says the tag defaults to its release" $?
printf '%s\n' "$bundled_env" | grep -xF 'COMPOSE_PROJECT_NAME=barako-cms' >/dev/null; check "the bundle's env example fixes the compose project name" $?
grep -qF 'Pin it' "$REPO/.env.prod.example"; check "the repository's env example keeps its own wording" $?
refuses bash scripts/release-assets-check.sh 9.8.7 "$WORK/out" --download-typo x; check "an unknown third argument fails" $?
refuses bash scripts/release-assets-check.sh 9.8.7 "$WORK/out" --download; check "--download without a tag fails" $?

refuses bash "$SRC/scripts/build-prod-bundle.sh" 'latest' "$WORK/out3"; check "a version that is not a version fails" $?
refuses bash "$SRC/scripts/build-prod-bundle.sh" '6.0.0;x' "$WORK/out3"; check "a version with a shell character fails" $?

printf 'tampered' >> "$WORK/out/barako-cms-prod-9.8.7.tar.gz"
refuses bash scripts/release-assets-check.sh 9.8.7 "$WORK/out"; check "the read-back fails when the archive does not match its sum" $?

# A mount the compose file declares and the bundle does not carry is the failure #1436 is about.
cp "$SRC/docker-compose.prod.yml" "$SRC/docker-compose.prod.yml.orig"
python3 - "$SRC/docker-compose.prod.yml" <<'PY'
import sys
p = sys.argv[1]
t = open(p).read()
old = "      - ./Caddyfile:/etc/caddy/Caddyfile:ro\n"
assert t.count(old) == 1
open(p, "w").write(t.replace(old, old + "      - ./extra.conf:/etc/extra.conf:ro\n"))
PY
msg="$(bash "$SRC/scripts/build-prod-bundle.sh" 9.8.7 "$WORK/out4" 2>&1 || true)"
printf '%s' "$msg" | grep -qF 'mounts ./extra.conf and the bundle does not carry it'; check "a mount the bundle does not carry fails the build, naming it" $?
cp "$SRC/docker-compose.prod.yml.orig" "$SRC/docker-compose.prod.yml"
python3 - "$SRC/docker-compose.prod.yml" <<'PY'
import sys
p = sys.argv[1]
t = open(p).read()
old = "for example 3.21.0}"
assert t.count(old) == 1
open(p, "w").write(t.replace(old, "for example 3.22.0}"))
PY
refuses bash "$SRC/scripts/build-prod-bundle.sh" 9.8.7 "$WORK/out5"; check "a reworded BARAKO_TAG line fails the build instead of shipping unpinned" $?
cp "$SRC/docker-compose.prod.yml.orig" "$SRC/docker-compose.prod.yml"
python3 - "$SRC/docker-compose.prod.yml" <<'PY'
import sys
p = sys.argv[1]
t = open(p).read()
old = "is required rather than defaulting to latest"
assert t.count(old) == 1
open(p, "w").write(t.replace(old, "is needed rather than defaulting to latest"))
PY
refuses bash "$SRC/scripts/build-prod-bundle.sh" 9.8.7 "$WORK/out7"; check "a reworded BARAKO_TAG comment fails the build instead of shipping the old text" $?
cp "$SRC/docker-compose.prod.yml.orig" "$SRC/docker-compose.prod.yml"
rm "$SRC/scripts/db-app-role.sql"
refuses bash "$SRC/scripts/build-prod-bundle.sh" 9.8.7 "$WORK/out6"; check "a missing script fails the build" $?

echo
echo "$pass passed, $fail failed"
[ "$fail" = 0 ]

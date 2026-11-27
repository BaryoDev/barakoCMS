#!/usr/bin/env bash
#
# Proves the production bundle runs with nothing around it (#1436).
#
#   scripts/prod-bundle-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). The bundle is built from this working tree
# with scripts/build-prod-bundle.sh, unpacked in an empty directory outside the checkout, and the
# stack is started from there, so a file the compose file mounts and the bundle misses shows up
# here and not on a customer's host.
#
#   1. the archive matches its SHA256SUMS
#   2. the compose file with no BARAKO_TAG in the environment resolves to the bundle's version
#   2b. the compose project name is barako-cms from .env, so the data volumes do not change name
#      when the next release unpacks into another directory
#   3. every bind mount the compose file declares exists in the unpacked directory (this covers
#      the Caddyfile, which this check does not start because Caddy needs ACME)
#   4. postgres starts, then app and db-backup, so compose runs the app role job through
#      depends_on, and both report healthy
#   5. the role job exited 0 and the backup job wrote a first archive from the mounted script
#
# The image is tagged with the bundle's version for the run, because that is what the bundle asks
# for. The extra tag is removed afterwards.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
VERSION="${BUNDLE_CHECK_VERSION:-0.0.0-ci}"
BUNDLED_IMAGE="ghcr.io/baryodev/barako-cms:$VERSION"
PROJECT="bundle-check-$$"
WORK="$(mktemp -d)"
DIR=""

step() { printf '\n=== %s\n' "$1"; }
dc() { (cd "$DIR" && docker compose -p "$PROJECT" --env-file "$DIR/.env" -f docker-compose.prod.yml "$@"); }
fail() {
  printf '\nFAILED: %s\n' "$1" >&2
  [ -z "$DIR" ] || dc logs --no-color --tail 60 app db-backup postgres postgres-extensions >&2 || true
  exit 1
}
cleanup() {
  [ -z "$DIR" ] || dc down -v --remove-orphans >/dev/null 2>&1 || true
  [ "$IMAGE" = "$BUNDLED_IMAGE" ] || docker rmi "$BUNDLED_IMAGE" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

docker image inspect "$IMAGE" >/dev/null 2>&1 || { echo "FAILED: image $IMAGE does not exist; build it first" >&2; exit 1; }
[ "$IMAGE" = "$BUNDLED_IMAGE" ] || docker tag "$IMAGE" "$BUNDLED_IMAGE"

step "building the bundle from this working tree"
bash "$REPO/scripts/build-prod-bundle.sh" "$VERSION" "$WORK/release"

step "the archive matches its SHA256SUMS"
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$WORK/release" && sha256sum -c SHA256SUMS)
else
  (cd "$WORK/release" && shasum -a 256 -c SHA256SUMS)
fi

step "unpacking into an empty directory"
mkdir "$WORK/empty"
[ -z "$(ls -A "$WORK/empty")" ] || fail "the target directory is not empty"
tar -xzf "$WORK/release/barako-cms-prod-$VERSION.tar.gz" -C "$WORK/empty"
DIR="$WORK/empty/barako-cms-prod-$VERSION"
[ -d "$DIR" ] || fail "the archive has no barako-cms-prod-$VERSION directory"
# A repository above the directory would let a missing file be found by accident.
if git -C "$DIR" rev-parse --git-dir >/dev/null 2>&1; then
  fail "the unpacked bundle sits inside a git repository, so it is not proof of an empty start"
fi

# The example file with its own values, plus the secrets the stack refuses to start without.
# BARAKO_TAG stays as the bundle wrote it, which is the point.
cp "$DIR/.env.prod.example" "$DIR/.env"
cat >> "$DIR/.env" <<ENV
DB_PASSWORD=$(openssl rand -hex 16)
DB_APP_PASSWORD=$(openssl rand -hex 16)
JWT_KEY=$(openssl rand -hex 32)
ADMIN_PASSWORD=Ci-$(openssl rand -hex 8)-Aa1!
BACKUP_CRON_SCHEDULE=* * * * *
BACKUP_SCHEMA_WAIT_SECONDS=120
ENV

step "with no BARAKO_TAG in the environment the app image is the bundle's version"
grep -v '^BARAKO_TAG=' "$DIR/.env" > "$WORK/no-tag.env"
resolved="$(cd "$DIR" && docker compose --env-file "$WORK/no-tag.env" -f docker-compose.prod.yml config --images | grep 'barako-cms')"
[ "$resolved" = "$BUNDLED_IMAGE" ] || fail "the compose file resolves to '$resolved', not $BUNDLED_IMAGE"
echo "$resolved"

step "the compose project name is the bundle's, not the directory's"
project_name="$(cd "$DIR" && docker compose --env-file "$DIR/.env" -f docker-compose.prod.yml config --format json \
  | python3 -c 'import json, sys; print(json.load(sys.stdin)["name"])')"
[ "$project_name" = "barako-cms" ] || fail "the project name is '$project_name', so the data volumes would follow the directory name"
echo "$project_name"

step "every bind mount the compose file declares is in the unpacked directory"
dc config --format json | python3 -c '
import json, os, sys
root = os.path.realpath(sys.argv[1])
bad = 0
for name, svc in sorted(json.load(sys.stdin)["services"].items()):
    for v in svc.get("volumes", []):
        if v.get("type") != "bind":
            continue
        src = os.path.realpath(v["source"])
        ok = src.startswith(root + os.sep) and os.path.exists(src) and (not os.path.isdir(src) or os.listdir(src))
        print(("ok   " if ok else "MISSING ") + name + ": " + os.path.relpath(src, root) + " -> " + v["target"])
        bad += 0 if ok else 1
sys.exit(1 if bad else 0)
' "$DIR" || fail "a bind mount points outside the bundle or at nothing"

step "starting postgres"
dc up -d --wait --wait-timeout 240 postgres || fail "postgres did not become healthy"

step "starting app and db-backup, so compose runs the app role job through depends_on"
limit=""
! command -v timeout >/dev/null || limit="timeout 300"
$limit docker compose -p "$PROJECT" --project-directory "$DIR" --env-file "$DIR/.env" -f "$DIR/docker-compose.prod.yml" up -d app db-backup \
  || fail "compose could not start app and db-backup (or the job did not finish within 300 seconds)"
for svc in app db-backup; do
  healthy=0
  for _ in $(seq 1 80); do
    status="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then healthy=1; break; fi
    sleep 3
  done
  [ "$healthy" = 1 ] || fail "$svc did not become healthy"
  echo "$svc is healthy"
done

step "the app role job exited 0"
job_state="$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$(dc ps -aq postgres-extensions)" 2>/dev/null || true)"
[ "$job_state" = "exited 0" ] || fail "the postgres-extensions container is '$job_state', not 'exited 0'"

step "the backup job wrote an archive from the mounted script"
ok=0
for _ in $(seq 1 60); do
  if dc logs --no-color db-backup 2>&1 | grep -q "Backup OK"; then ok=1; break; fi
  sleep 3
done
[ "$ok" = 1 ] || fail "db-backup never logged a successful backup"

printf '\nall checks passed\n'

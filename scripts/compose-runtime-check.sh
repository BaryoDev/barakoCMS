#!/usr/bin/env bash
#
# Starts the hardened production stack from docker-compose.prod.yml and proves it still works.
#
#   scripts/compose-runtime-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). Caddy is left out because it needs ACME.
#
#   1. postgres, app and db-backup start on a fresh volume and report healthy (up --wait)
#   2. postgres-extensions runs to completion on its own, because an exited one-shot counts as a
#      failure for --wait
#   3. the probe built into the binary answers on /health/ready, inside the container
#   4. the backup's cron job runs once a minute and writes an archive with the root file system
#      read-only
#   5. no read-only file system or permission error is in the app, backup or database logs
#   6. a second start of the app with an example JWT key, and then with an example database
#      password, exits non-zero and names the setting without printing the value
#
# The core image has no upload route, so no upload is exercised here. /tmp and the home directory
# are writable tmpfs mounts for the buffering a module image would do.
set -euo pipefail

cd "$(dirname "$0")/.."
IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
TAG="${IMAGE##*:}"
PROJECT="compose-runtime-$$"
ENVFILE="$(mktemp)"
LOGS="$(mktemp)"

cat .env.prod.example > "$ENVFILE"
cat >> "$ENVFILE" <<ENV
BARAKO_TAG=$TAG
DB_PASSWORD=$(openssl rand -hex 16)
JWT_KEY=$(openssl rand -hex 32)
ADMIN_PASSWORD=Ci-$(openssl rand -hex 8)-Aa1!
BACKUP_CRON_SCHEDULE=* * * * *
BACKUP_SCHEMA_WAIT_SECONDS=120
ENV

dc() { docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml "$@"; }

cleanup() {
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  rm -f "$ENVFILE" "$LOGS"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; dc logs --no-color --tail 60 app db-backup postgres >&2 || true; exit 1; }

step "starting postgres, app and db-backup on a fresh volume"
dc up -d --wait --wait-timeout 240 postgres app db-backup || fail "the stack did not become healthy"

step "running the extensions one-shot"
dc run --rm --no-deps postgres-extensions || fail "postgres-extensions did not finish cleanly"

step "the probe answers on /health/ready"
dc exec -T app dotnet barakoCMS.dll --health-probe /health/ready || fail "the readiness probe failed"

step "the backup writes an archive on a read-only root"
ok=0
for _ in $(seq 1 60); do
  if dc logs --no-color db-backup 2>&1 | grep -q "Backup OK"; then ok=1; break; fi
  sleep 3
done
[ "$ok" = 1 ] || fail "db-backup never logged a successful backup"

step "no read-only or permission errors in the logs"
dc logs --no-color app db-backup postgres > "$LOGS" 2>&1 || true
if grep -iE "read-only file system|permission denied|EROFS|UnauthorizedAccessException|Access to the path" "$LOGS"; then
  fail "a service hit a read-only or permission error"
fi

refused() { # name, expected phrase, env overrides...
  local name="$1" phrase="$2" out rc=0
  shift 2
  out=$(dc run --rm --no-deps -T "$@" app 2>&1) || rc=$?
  [ "$rc" -ne 0 ] || fail "$name: the app started with an example value"
  printf '%s' "$out" | grep -qF "$phrase" || fail "$name: the error does not name the setting ($phrase)"
  step "$name refused with exit $rc"
}

step "example secrets are refused outside Development"
refused "example JWT key" "JWT:Key" -e "JWT__Key=change-me-to-at-least-32-characters-secret"
refused "example database password" "database password" \
  -e "ConnectionStrings__DefaultConnection=Host=postgres;Database=barako_cms;Username=postgres;Password=change-me-to-a-strong-password"

printf '\nall checks passed\n'

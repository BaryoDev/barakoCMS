#!/usr/bin/env bash
#
# Starts the hardened production stack from docker-compose.prod.yml and proves it still works.
#
#   scripts/compose-runtime-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). Caddy is left out because it needs ACME.
#
#   1. postgres starts on a fresh volume and reports healthy (up --wait)
#   2. a plain up of app and db-backup (no --no-deps, no --wait) makes compose run
#      postgres-extensions, the job that runs scripts/db-app-role.sql, through depends_on. Health is
#      polled, and the job's container must have exited 0
#   3. the app's sessions in pg_stat_activity use the app role, not the owner
#   4. the probe built into the binary answers on /health/ready, inside the container
#   5. the backup's cron job runs once a minute and writes an archive with the root file system
#      read-only
#   6. no read-only file system or permission error is in the app, backup or database logs
#   7. a second start of the app with an example JWT key, and then with an example database
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
DB_APP_PASSWORD=$(openssl rand -hex 16)
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
fail() { printf '\nFAILED: %s\n' "$1" >&2; dc logs --no-color --tail 60 app db-backup postgres postgres-extensions >&2 || true; exit 1; }

step "starting postgres on a fresh volume"
dc up -d --wait --wait-timeout 240 postgres || fail "postgres did not become healthy"

step "starting app and db-backup, so compose runs the app role job through depends_on"
# A plain up, not --no-deps: this is the path an operator takes. No --wait either, because a
# dependency that finished and exited may be reported as a failure by --wait (it depends on the
# compose version), so health is polled below.
limit=""
! command -v timeout > /dev/null || limit="timeout 300"
# A hung job would otherwise wait for the CI job timeout. The deadline is only for the up itself.
$limit docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml up -d app db-backup || fail "compose could not start app and db-backup (or the job did not finish within 300 seconds)"
for svc in app db-backup; do
  healthy=0
  for _ in $(seq 1 80); do
    status="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then healthy=1; break; fi
    sleep 3
  done
  [ "$healthy" = 1 ] || fail "$svc did not become healthy"
done

step "the app role job exited 0"
job_state="$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$(dc ps -aq postgres-extensions)" 2>/dev/null || true)"
[ "$job_state" = "exited 0" ] || fail "the postgres-extensions container is '$job_state', not 'exited 0'"

step "the probe answers on /health/ready"
dc exec -T app dotnet barakoCMS.dll --health-probe /health/ready || fail "the readiness probe failed"

step "the API's sessions use the app role"
# Not the owner: a superuser session would bypass row level security. The API is found by its
# address on the project network, so the other services' sessions are not counted.
api_ips="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$(dc ps -q app)")" \
  || fail "could not inspect the app container"
[ -n "${api_ips// /}" ] || fail "could not find the API's address"
# The query goes in on stdin, because psql does not substitute :'ips' inside a -c string.
# Poll up to 30 seconds for at least one API session. A role other than barako_app fails at once.
roles=""
deadline=$((SECONDS + 30))
while :; do
  roles="$(printf '%s\n' "select distinct usename from pg_stat_activity where client_addr = any(string_to_array(btrim(:'ips'), ' ')::inet[]) order by 1;" \
    | dc exec -T postgres psql -X -At -U postgres -d barako_cms -v ON_ERROR_STOP=1 -v ips="$api_ips")" \
    || fail "could not read pg_stat_activity"
  if [ -n "$roles" ]; then
    [ "$roles" = "barako_app" ] || fail "the API's sessions use '$(printf '%s' "$roles" | tr '\n' ' ')', not only barako_app"
    break
  fi
  [ "$SECONDS" -lt "$deadline" ] || fail "no API session showed up in pg_stat_activity within 30 seconds"
  sleep 2
done

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
  # A rule that stopped working would start the app and keep it running, so cap the wait.
  local limit=""
  ! command -v timeout > /dev/null || limit="timeout 60"
  out=$($limit docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml run --rm --no-deps -T "$@" app 2>&1) || rc=$?
  [ "$rc" -ne 124 ] || fail "$name: the app was still running after 60 seconds, so the guard did not stop it"
  [ "$rc" -ne 0 ] || fail "$name: the app started with an example value"
  printf '%s' "$out" | grep -qF "$phrase" || fail "$name: the error does not name the setting ($phrase)"
  step "$name refused with exit $rc"
}

step "example secrets are refused outside Development"
refused "example JWT key" "JWT:Key" -e "JWT__Key=change-me-to-at-least-32-characters-secret"
refused "example database password" "database password" \
  -e "ConnectionStrings__DefaultConnection=Host=postgres;Database=barako_cms;Username=barako_app;Password=change-me-to-a-strong-password"

printf '\nall checks passed\n'

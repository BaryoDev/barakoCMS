#!/usr/bin/env bash
#
# Proves docker-compose.secrets.yml works: the stack starts with its secrets in files and none of
# them in the environment, and the prod file without the override does not start on that same env.
#
#   scripts/compose-secrets-runtime-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). Caddy is left out because it needs ACME.
# Generated values are never printed. ./secrets/ is created here and removed on exit.
#
# After the checks it starts the app once with a db_connection_string whose password differs from
# db_app_password. The app must not become healthy, its log must show an authentication failure,
# and no password may appear in any service's log.
set -euo pipefail

cd "$(dirname "$0")/.."
# shellcheck source=scripts/lib-db-role-check.sh
. scripts/lib-db-role-check.sh
IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
TAG="${IMAGE##*:}"
PROJECT="compose-secrets-$$"
ENVFILE="$(mktemp)"
LOGS="$(mktemp)"

[ ! -e secrets ] || { echo "secrets/ already exists, refusing to overwrite or delete it" >&2; rm -f "$ENVFILE" "$LOGS"; exit 2; }

# No JWT_KEY, ADMIN_PASSWORD, DB_PASSWORD or DB_APP_PASSWORD here, on purpose.
cat .env.prod.example > "$ENVFILE"
cat >> "$ENVFILE" <<ENV
BARAKO_TAG=$TAG
SECRETS_AS_FILES=true
BACKUP_CRON_SCHEDULE=* * * * *
BACKUP_SCHEMA_WAIT_SECONDS=120
ENV

DC=(docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml -f docker-compose.secrets.yml)
with_override() { "${DC[@]}" "$@"; }
dc() { with_override "$@"; }
CHECK_DB=barako_cms
CHECK_API=app
without_override() { docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml "$@"; }

# Each step has its own || true so one failure does not skip the rest. Checks never get one.
cleanup() {
  with_override down -v --remove-orphans >/dev/null 2>&1 || true
  sudo rm -rf secrets || true
  rm -f "$ENVFILE" "$LOGS" || true
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
# fail prints a log tail, so it goes through redact (scripts/lib-db-role-check.sh), which hides every
# value in SECRET_VALUES. Set once the secret files exist; the last phase adds the wrong one.
fail() {
  printf '\nFAILED: %s\n' "$1" >&2
  with_override logs --no-color --tail 60 app db-backup postgres postgres-extensions 2>&1 | redact >&2 || true
  exit 1
}

step "writing the secret files, as docs/deploy-in-production.md says"
mkdir secrets
openssl rand -hex 24 | tr -d '\n' > secrets/jwt_key
openssl rand -hex 24 | tr -d '\n' > secrets/admin_password
openssl rand -hex 24 | tr -d '\n' > secrets/db_password
openssl rand -hex 24 | tr -d '\n' > secrets/db_app_password
# The API connects as the app role, so this holds its name and its password, not the owner's.
printf 'Host=postgres;Database=barako_cms;Username=barako_app;Password=%s' "$(cat secrets/db_app_password)" > secrets/db_connection_string
# Read before the chown, the files are not ours after it.
ADMIN_PASSWORD_VALUE="$(cat secrets/admin_password)"
APP_PASSWORD="$(cat secrets/db_app_password)"
SECRET_VALUES=("$APP_PASSWORD" "$(cat secrets/db_password)" "$ADMIN_PASSWORD_VALUE" "$(cat secrets/jwt_key)")
sudo chown 1654:1654 secrets/*
sudo chmod 0440 secrets/*

step "without the override, SECRETS_AS_FILES alone must not leave a healthy app or an initialised database"
# The file has to resolve, so an interpolation error fails this script instead of passing the case.
without_override config --quiet || fail "the prod file does not resolve with SECRETS_AS_FILES and no override"
if without_override up -d --wait --wait-timeout 90 postgres app > "$LOGS" 2>&1; then
  fail "the prod file started on SECRETS_AS_FILES with no override"
fi
without_override logs --no-color postgres > "$LOGS" 2>&1 || true
grep -q "superuser password is not specified" "$LOGS" || fail "postgres did not refuse for the missing password"
without_override ps --status running --format '{{.Service}}' | grep -qx app && fail "the app is running without its secrets"
without_override ps --status running --format '{{.Service}}' | grep -qx postgres && fail "postgres initialised without a password"
without_override down -v --remove-orphans > /dev/null 2>&1 || true

step "with the override, postgres starts on a fresh volume and reports healthy"
with_override up -d --wait --wait-timeout 240 postgres || fail "postgres did not become healthy"

step "app and db-backup start, compose runs the app role job through depends_on, and they report healthy"
# A plain up, not --no-deps: this is the path an operator takes. No --wait either, because a
# dependency that finished and exited may be reported as a failure by --wait (it depends on the
# compose version), so health is polled.
limit=""
! command -v timeout > /dev/null || limit="timeout 300"
# A hung job would otherwise wait for the CI job timeout. The deadline is only for the up itself.
$limit "${DC[@]}" up -d app db-backup || fail "compose could not start app and db-backup (or the job did not finish within 300 seconds)"
for svc in app db-backup; do
  healthy=0
  for _ in $(seq 1 80); do
    status="$(docker inspect -f '{{.State.Health.Status}}' "$(with_override ps -q "$svc")" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then healthy=1; break; fi
    sleep 3
  done
  [ "$healthy" = 1 ] || fail "$svc did not become healthy"
done

step "the app role job, which reads both passwords from files, exited 0"
job_state="$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$(with_override ps -aq postgres-extensions)" 2>/dev/null || true)"
[ "$job_state" = "exited 0" ] || fail "the postgres-extensions container is '$job_state', not 'exited 0'"

step "the initial admin signs in with the password from secrets/admin_password"
# An empty admin password makes the seeder generate one, so a healthy app proves nothing about the
# file. The body goes in on stdin, and neither the password nor the token is printed. The API has no
# published port, so the request comes from a throwaway container on the project network, pinned by
# digest like the images in the compose files (CURL_IMAGE, from scripts/lib-db-role-check.sh).
login() {
  printf '{"username":"admin","password":"%s"}' "$1" \
    | docker run --rm -i --network "${PROJECT}_default" "$CURL_IMAGE" -s -o /dev/null -w '%{http_code}' \
        -H 'Content-Type: application/json' --data-binary @- http://app:8080/api/auth/login
}
[ "$(login "$ADMIN_PASSWORD_VALUE")" = "200" ] || fail "the initial admin could not sign in with secrets/admin_password"
[ "$(login "not-the-admin-password")" != "200" ] || fail "the sign-in check accepts any password"

check_app_role_runtime

step "the backup reads its password from the file and writes an archive"
ok=0
for _ in $(seq 1 60); do
  with_override logs --no-color db-backup > "$LOGS" 2>&1 || true
  if grep -q "Backup OK" "$LOGS"; then ok=1; break; fi
  sleep 3
done
[ "$ok" = 1 ] || fail "db-backup never logged a successful backup"

step "no read-only or permission errors in the logs"
with_override logs --no-color app db-backup postgres > "$LOGS" 2>&1 || true
if grep -iE "read-only file system|permission denied|EROFS|UnauthorizedAccessException|Access to the path|cannot be read" "$LOGS"; then
  fail "a service hit a read-only or permission error"
fi

step "a db_connection_string with the wrong password: the app does not become healthy"
# Written the way the secret files were: same path, mode 0440, owner 1654, through sudo. tee keeps
# the file's owner and mode.
WRONG_PASSWORD="wrong-$(openssl rand -hex 16)"
SECRET_VALUES+=("$WRONG_PASSWORD")
with_override stop app > /dev/null || fail "could not stop the app"
printf 'Host=postgres;Database=barako_cms;Username=barako_app;Password=%s' "$WRONG_PASSWORD" \
  | sudo tee secrets/db_connection_string > /dev/null
sudo chown 1654:1654 secrets/db_connection_string
sudo chmod 0440 secrets/db_connection_string
with_override up -d app > /dev/null 2>&1 || true
# The window is 75 seconds. The app must never report healthy in it, and by the end its log must
# show the database refusing the password.
auth_failed=0
deadline=$((SECONDS + 75))
while [ "$SECONDS" -lt "$deadline" ]; do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$(with_override ps -aq app | head -1)" 2>/dev/null || true)"
  [ "$status" != "healthy" ] || fail "the app became healthy with a password that is not the app role's"
  sleep 3
done
with_override logs --no-color app > "$LOGS" 2>&1 || true
grep -q "password authentication failed" "$LOGS" && auth_failed=1
[ "$auth_failed" = 1 ] || fail "the app log shows no 'password authentication failed' within 75 seconds"

step "no password is in any service's log"
for svc in postgres postgres-extensions app db-backup; do
  with_override logs --no-color "$svc" > "$LOGS" 2>&1 || true
  for v in "${SECRET_VALUES[@]}"; do
    # grep -qF on a captured file: nothing but the service's name is printed on a hit.
    if [ -n "$v" ] && grep -qF -- "$v" "$LOGS"; then
      # No log tail here: the line holds a password. Only the service's name is printed.
      printf '\nFAILED: a password is in a service log\nfound in %s\n' "$svc" >&2
      exit 1
    fi
  done
done

printf '\nall checks passed\n'

#!/usr/bin/env bash
#
# Proves docker-compose.secrets.yml works: the stack starts with its secrets in files and none of
# them in the environment, and the prod file without the override does not start on that same env.
#
#   scripts/compose-secrets-runtime-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). Caddy is left out because it needs ACME.
# Generated values are never printed. ./secrets/ is created here and removed on exit.
set -euo pipefail

cd "$(dirname "$0")/.."
IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
TAG="${IMAGE##*:}"
PROJECT="compose-secrets-$$"
ENVFILE="$(mktemp)"
LOGS="$(mktemp)"

[ ! -e secrets ] || { echo "secrets/ already exists, refusing to overwrite or delete it" >&2; rm -f "$ENVFILE" "$LOGS"; exit 2; }

# No JWT_KEY, ADMIN_PASSWORD or DB_PASSWORD here, on purpose.
cat .env.prod.example > "$ENVFILE"
cat >> "$ENVFILE" <<ENV
BARAKO_TAG=$TAG
SECRETS_AS_FILES=true
BACKUP_CRON_SCHEDULE=* * * * *
BACKUP_SCHEMA_WAIT_SECONDS=120
ENV

with_override() { docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml -f docker-compose.secrets.yml "$@"; }
without_override() { docker compose -p "$PROJECT" --env-file "$ENVFILE" -f docker-compose.prod.yml "$@"; }

# Each step has its own || true so one failure does not skip the rest. Checks never get one.
cleanup() {
  with_override down -v --remove-orphans >/dev/null 2>&1 || true
  sudo rm -rf secrets || true
  rm -f "$ENVFILE" "$LOGS" || true
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; with_override logs --no-color --tail 60 app db-backup postgres >&2 || true; exit 1; }

step "writing the secret files, as docs/deploy-in-production.md says"
mkdir secrets
openssl rand -hex 24 | tr -d '\n' > secrets/jwt_key
openssl rand -hex 24 | tr -d '\n' > secrets/admin_password
openssl rand -hex 24 | tr -d '\n' > secrets/db_password
printf 'Host=postgres;Database=barako_cms;Username=postgres;Password=%s' "$(cat secrets/db_password)" > secrets/db_connection_string
ADMIN_PASSWORD_VALUE="$(cat secrets/admin_password)" # read before the chown, the file is not ours after it
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

step "with the override, postgres, app and db-backup start on a fresh volume and report healthy"
with_override up -d --wait --wait-timeout 240 postgres app db-backup || fail "the stack did not become healthy"

step "the initial admin signs in with the password from secrets/admin_password"
# An empty admin password makes the seeder generate one, so a healthy app proves nothing about the
# file. The body goes in on stdin, and neither the password nor the token is printed. The API has no
# published port, so the request comes from a throwaway container on the project network.
login() {
  printf '{"username":"admin","password":"%s"}' "$1" \
    | docker run --rm -i --network "${PROJECT}_default" curlimages/curl:8.11.1 -s -o /dev/null -w '%{http_code}' \
        -H 'Content-Type: application/json' --data-binary @- http://app:8080/api/auth/login
}
[ "$(login "$ADMIN_PASSWORD_VALUE")" = "200" ] || fail "the initial admin could not sign in with secrets/admin_password"
[ "$(login "not-the-admin-password")" != "200" ] || fail "the sign-in check accepts any password"

step "running the extensions one-shot"
with_override run --rm --no-deps postgres-extensions || fail "postgres-extensions did not finish cleanly"

step "the backup reads its password from the file and writes an archive"
ok=0
for _ in $(seq 1 60); do
  if with_override logs --no-color db-backup 2>&1 | grep -q "Backup OK"; then ok=1; break; fi
  sleep 3
done
[ "$ok" = 1 ] || fail "db-backup never logged a successful backup"

step "no read-only or permission errors in the logs"
with_override logs --no-color app db-backup postgres > "$LOGS" 2>&1 || true
if grep -iE "read-only file system|permission denied|EROFS|UnauthorizedAccessException|Access to the path|cannot be read" "$LOGS"; then
  fail "a service hit a read-only or permission error"
fi

printf '\nall checks passed\n'

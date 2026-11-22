#!/usr/bin/env bash
#
# Starts the quickstart's owner job and API from a copy of quickstart/ and proves the API runs as
# the app role.
#
#   scripts/quickstart-runtime-check.sh [image]      # default ghcr.io/baryodev/barako-cms:ci
#
# The image has to exist already (CI builds it first). The quickstart folder is copied out of the
# checkout, as its README says to do, and started with a throwaway .env. The api's host port is
# reset in an override, so no fixed port is taken on the runner, and the API is reached from a
# curl container on the project network. `ports: !reset []` needs Docker Compose 2.24 or later;
# the version is printed, and this fails if it is older.
#
# Then the checks in scripts/lib-db-role-check.sh run against the quickstart's database, barakocms.
#
# scripts/quickstart-backup-check.sh covers the quickstart's backup and is not changed by this.
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$PWD"
# shellcheck source=scripts/lib-db-role-check.sh
. scripts/lib-db-role-check.sh

IMAGE="${1:-ghcr.io/baryodev/barako-cms:ci}"
TAG="${IMAGE##*:}"
PROJECT="qs-runtime-$$"
WORK="$(mktemp -d)"
QS="$WORK/quickstart"
LOGS="$(mktemp)"

APP_PASSWORD="qs-app-$(openssl rand -hex 16)"
DB_PASSWORD_VALUE="qs-db-$(openssl rand -hex 16)"
ADMIN_PASSWORD_VALUE="Ci-$(openssl rand -hex 8)-Aa1!"
SECRET_VALUES=("$APP_PASSWORD" "$DB_PASSWORD_VALUE" "$ADMIN_PASSWORD_VALUE")

cp -R "$REPO/quickstart" "$QS"
cat > "$QS/.env" <<ENV
BARAKO_TAG=$TAG
DB_PASSWORD=$DB_PASSWORD_VALUE
DB_APP_PASSWORD=$APP_PASSWORD
JWT_KEY=$(openssl rand -hex 32)
ADMIN_PASSWORD=$ADMIN_PASSWORD_VALUE
ENV
cat > "$QS/ci-override.yml" <<'YML'
services:
  api:
    ports: !reset []
YML

DC=(docker compose -p "$PROJECT" --project-directory "$QS" --env-file "$QS/.env" -f "$QS/docker-compose.yml" -f "$QS/ci-override.yml")
dc() { "${DC[@]}" "$@"; }
CHECK_DB=barakocms
CHECK_API=api
# The quickstart answers only to localhost and 127.0.0.1 unless ALLOWED_HOSTS says otherwise.
API_HOST_HEADER=localhost

cleanup() {
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$WORK"
  rm -f "$LOGS"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
# The throwaway passwords are not in any service's output, but the tail is filtered anyway.
fail() {
  printf '\nFAILED: %s\n' "$1" >&2
  dc logs --no-color --tail 60 api postgres postgres-extensions 2>&1 \
    | redact >&2 || true
  exit 1
}

step "docker compose version"
compose_version="$(docker compose version --short)"
printf 'docker compose %s\n' "$compose_version"
oldest="$(printf '%s\n%s\n' "2.24.0" "${compose_version#v}" | sort -V | head -1)"
[ "$oldest" = "2.24.0" ] || fail "docker compose $compose_version is older than 2.24, which the ports override needs"

step "the copied quickstart resolves and publishes no host port"
dc config --quiet || fail "the copied compose file does not resolve"
if dc config | grep -q 'published:'; then
  fail "the api still publishes a host port after the override"
fi

step "starting the api, so compose runs the owner job through depends_on"
# A plain up of the api: its depends_on starts postgres, waits for it to be healthy, and runs the
# owner job to completion first. No --wait, which may treat an exited dependency as a failure.
limit=""
! command -v timeout > /dev/null || limit="timeout 300"
$limit "${DC[@]}" up -d api || fail "compose could not start the api (or the job did not finish within 300 seconds)"

step "the api reports healthy"
healthy=0
deadline=$((SECONDS + 240))
while [ "$SECONDS" -lt "$deadline" ]; do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q api)" 2>/dev/null || true)"
  if [ "$status" = "healthy" ]; then healthy=1; break; fi
  sleep 3
done
[ "$healthy" = 1 ] || fail "the api did not become healthy within 240 seconds"

step "the owner job exited 0"
job_state="$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$(dc ps -aq postgres-extensions)" 2>/dev/null || true)"
[ "$job_state" = "exited 0" ] || fail "the postgres-extensions container is '$job_state', not 'exited 0'"

step "the admin signs in over the project network"
body="$(printf '{"username":"admin","password":"%s"}' "$ADMIN_PASSWORD_VALUE" | api_call POST /api/auth/login)" \
  || fail "could not reach the api from the project network"
[ "$(api_status "$body")" = "200" ] || fail "the admin could not sign in (status $(api_status "$body"))"

check_app_role_runtime

printf '\nall checks passed\n'

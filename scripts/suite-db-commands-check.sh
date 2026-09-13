#!/usr/bin/env bash
#
# Proves the Suite host, the one the published image runs, answers the schema commands.
#
# The schema refusal and docs/upgrading-to-4.0.md tell an operator to run db-assert and db-patch.
# scripts/upgrade-check.sh proves those against barakoCMS.dll, the core host, which is not what
# ghcr.io/baryodev/barako-cms runs. Its entrypoint is BarakoCMS.Suite.dll, and until #662 the Suite
# ignored its arguments: `db-assert` booted the web app against the database instead of checking it.
#
# The sequence, against an empty database in Production mode (so the store runs CreateOnly):
#
#   1. db-assert must FAIL, promptly, must not start a web server, and must not apply the schema
#   2. db-patch must write a non-empty script and must not apply the schema
#   3. db-apply must succeed and create the schema
#   4. db-assert must PASS
#
# "Must not apply the schema" is checked on mt_doc_users rather than on an empty database. Resolving
# the host's services creates mt_doc_jobs on the core host and the Suite alike, before any command
# runs, which is a separate matter from whether the command was honoured.
#
# Step 1 is the one that fails without the fix: the old Suite never exits, so the timeout catches it.
#
# Usage: scripts/suite-db-commands-check.sh

set -euo pipefail

cd "$(dirname "$0")/.."

PG="suite-db-commands-pg"
PG_PORT="${PG_PORT:-55436}"
APP_PORT="${APP_PORT:-58096}"
DB="barako_suite_db_commands"
JWT_KEY='suite-db-commands-key-that-is-at-least-32-chars-long'
COMMAND_TIMEOUT="${COMMAND_TIMEOUT:-180}"
WORK="$(mktemp -d)"
CONN="Host=127.0.0.1;Port=${PG_PORT};Database=${DB};Username=postgres;Password=postgres"

cleanup() {
    docker rm -f "$PG" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

run_suite() {
    # Explicit environment, not an inherited one, for the same reason as upgrade-check.sh: a stray
    # connection string in the shell would point the host at some other database.
    env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
        ASPNETCORE_ENVIRONMENT=Production \
        ASPNETCORE_URLS="http://127.0.0.1:${APP_PORT}" \
        ConnectionStrings__DefaultConnection="$CONN" \
        JWT__Key="$JWT_KEY" \
        SKIP_SEEDER=true \
        Kubernetes__Enabled=false \
        timeout "$COMMAND_TIMEOUT" dotnet exec "$WORK/publish/BarakoCMS.Suite.dll" "$@"
}

schema_applied() {
    docker exec "$PG" psql -U postgres -d "$DB" -tAc "select to_regclass('public.mt_doc_users') is not null;"
}

step "publishing the Suite from the working tree"
dotnet publish BarakoCMS.Suite/BarakoCMS.Suite.csproj -c Release -o "$WORK/publish" --nologo -v q \
    -clp:ErrorsOnly -p:RestoreLockedMode=true -nodeReuse:false

step "starting postgres"
docker run -d --name "$PG" -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    -p "127.0.0.1:${PG_PORT}:5432" postgres:16-alpine >/dev/null
# Over TCP: the image's bootstrap server answers on the socket before the real one is up.
for _ in $(seq 1 60); do docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 2; done
docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || fail "postgres never became ready"

step "db-assert must refuse an empty database, without serving"
set +e
run_suite db-assert >"$WORK/assert-before.log" 2>&1
status=$?
set -e
tail -15 "$WORK/assert-before.log"
if grep -q "Now listening on" "$WORK/assert-before.log"; then
    fail "db-assert started the web server, so the Suite ignored the command"
fi
[ "$status" -ne 124 ] || fail "db-assert did not exit within ${COMMAND_TIMEOUT}s, so the Suite ignored the command"
[ "$status" -ne 0 ] || fail "db-assert passed against an empty database"
[ "$(schema_applied)" = "f" ] || fail "the schema exists after db-assert, so something applied it first"
echo "refused with exit $status, schema not applied"

step "db-patch must write the delta and change nothing"
run_suite db-patch "$WORK/upgrade.sql" >"$WORK/patch.log" 2>&1 || { tail -30 "$WORK/patch.log"; fail "db-patch failed"; }
[ -s "$WORK/upgrade.sql" ] || fail "db-patch wrote no script"
grep -qi "create table" "$WORK/upgrade.sql" || fail "db-patch wrote a script with no CREATE TABLE in it"
[ "$(schema_applied)" = "f" ] || fail "the schema exists after db-patch, so it changed the database"
echo "wrote $(wc -l < "$WORK/upgrade.sql") lines, schema not applied"

step "db-apply must apply it"
run_suite db-apply >"$WORK/apply.log" 2>&1 || { tail -30 "$WORK/apply.log"; fail "db-apply failed"; }
[ "$(schema_applied)" = "t" ] || fail "db-apply succeeded and did not create the schema"
echo "schema applied"

step "db-assert must now pass"
run_suite db-assert >"$WORK/assert-after.log" 2>&1 || { tail -30 "$WORK/assert-after.log"; fail "db-assert failed after db-apply"; }
echo "passed"

printf '\nThe Suite host, the published image entrypoint, runs db-assert, db-patch and db-apply.\n'

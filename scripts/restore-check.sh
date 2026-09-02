#!/usr/bin/env bash
#
# Proves a backup can be restored and that the system comes back.
#
# The deliverable for a release is not "a backup file appears". It is "we have restored one and it
# worked". Until this existed, the only exercised backup path was the development compose stack and
# no restore had been run anywhere, which matters most right before an upgrade, because a failed
# upgrade's remedy is a restore.
#
# The sequence:
#
#   1. stand up Postgres and boot the app so there is a real schema with real content
#   2. take a backup with the same scripts/backup-cron.sh the deployments run, not a bare pg_dump
#   3. destroy the database completely
#   4. restore the archive into an empty one
#   5. boot the app against the restored database and assert the content is there and readable
#
# Step 3 is a drop, not a truncate. Restoring over a database that still has its schema can succeed
# for the wrong reason: the objects the dump failed to carry are already present.
#
# Usage: scripts/restore-check.sh

set -euo pipefail

PG="restore-check-pg"
PG_PORT="${PG_PORT:-55434}"
APP_PORT="${APP_PORT:-58095}"
DB="barako_restore_check"
JWT_KEY='restore-check-key-that-is-at-least-32-chars-long'
ADMIN_PASSWORD='RestoreCheck!123'
WORK="$(mktemp -d)"
CONN="Host=127.0.0.1;Port=${PG_PORT};Database=${DB};Username=postgres;Password=postgres"

cleanup() {
    if [ -n "${HOST_PID:-}" ]; then kill "$HOST_PID" 2>/dev/null || true; wait "$HOST_PID" 2>/dev/null || true; fi
    docker rm -f "$PG" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

if command -v lsof >/dev/null 2>&1; then
    port_in_use() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
elif command -v ss >/dev/null 2>&1; then
    port_in_use() { ss -ltn "sport = :$1" 2>/dev/null | grep -q LISTEN; }
else
    echo "note: neither lsof nor ss is available, so the port check is skipped" >&2
    port_in_use() { return 1; }
fi

for port in "$PG_PORT" "$APP_PORT"; do
    if port_in_use "$port"; then
        fail "port $port is already in use; something else would answer the checks below"
    fi
done

start_host() {
    # exec so the recorded PID is dotnet itself. Backgrounding the function without it makes $! the
    # wrapping subshell, and killing that leaves the host holding the port, which then answers the
    # health check for the next boot and every assertion after it describes the wrong process.
    exec env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
        ASPNETCORE_ENVIRONMENT=Production \
        ASPNETCORE_URLS="http://127.0.0.1:${APP_PORT}" \
        ConnectionStrings__DefaultConnection="$CONN" \
        JWT__Key="$JWT_KEY" \
        InitialAdmin__Username=admin \
        InitialAdmin__Password="$ADMIN_PASSWORD" \
        Kubernetes__Enabled=false \
        Seed__DemoContent=true \
        dotnet exec "$WORK/publish/barakoCMS.dll" "$@"
}

wait_for_health() {
    for _ in $(seq 1 60); do
        [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${APP_PORT}/health" || true)" = "200" ] && return 0
        kill -0 "$HOST_PID" 2>/dev/null || { cat "$WORK/$1" >&2; fail "the host exited during startup"; }
        sleep 2
    done
    cat "$WORK/$1" >&2
    fail "the host never became healthy"
}

psql_q() { docker exec "$PG" psql -U postgres -d "$DB" -tAc "$1"; }

step "building the host"
dotnet publish barakoCMS/barakoCMS.csproj -c Release -o "$WORK/publish" --nologo -v q -clp:ErrorsOnly

step "starting postgres"
docker run -d --name "$PG" -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=postgres -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
# pg_isready over the Unix socket is satisfied by the WRONG server. The postgres image boots a
# temporary initdb instance on the socket only, creates the database, shuts it down, and then starts
# the real one listening on TCP. So a socket check succeeds during bootstrap, the wait breaks early,
# and the verification a moment later lands in the shutdown window and reports that postgres never
# became ready. The whole failure takes four seconds out of a two minute budget.
#
# -h 127.0.0.1 forces TCP, which only the real server accepts. Same loop, right server.
for _ in $(seq 1 60); do docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 2; done
docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 || {
    docker logs "$PG" 2>&1 | tail -20 >&2; fail "postgres never became ready"
}

step "booting the app so there is a schema and seeded content"
start_host >"$WORK/first.log" 2>&1 &
HOST_PID=$!
wait_for_health first.log

# The seeder runs in the background after the host is already answering /health, so this waits for
# it rather than reading once. A count of zero here would make every comparison below vacuous.
#
# start_host asks for the demo content explicitly. This boots as Production, where the seed is off by
# default, and the restore is only worth anything if there are rows to lose.
for _ in $(seq 1 40); do
    CONTENT_BEFORE=$(psql_q "select count(*) from public.mt_doc_contents;" 2>/dev/null || echo 0)
    [ "${CONTENT_BEFORE:-0}" -gt 0 ] && break
    sleep 2
done
USERS_BEFORE=$(psql_q "select count(*) from public.mt_doc_users;")
[ "${CONTENT_BEFORE:-0}" -gt 0 ] || fail "no content was seeded, so a restore would prove nothing"
echo "$CONTENT_BEFORE content rows, $USERS_BEFORE users"

kill "$HOST_PID"; wait "$HOST_PID" 2>/dev/null || true; HOST_PID=""
# Wait for the port, not just the process: the assertions after the restore are worthless if the
# next boot silently fails to bind and the old host answers them.
for _ in $(seq 1 30); do port_in_use "$APP_PORT" || break; sleep 1; done
port_in_use "$APP_PORT" && fail "the first host is still holding port $APP_PORT"


step "taking a backup with the script the deployments run"
docker exec -e POSTGRES_HOST=localhost -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=postgres -e BACKUP_CRON_SCHEDULE="0 2 * * *" -e BACKUP_KEEP_DAYS=7 \
    "$PG" sh -c 'mkdir -p /backups /scripts' >/dev/null
docker cp scripts/backup-cron.sh "$PG:/scripts/backup-cron.sh" >/dev/null

# The daemon loops on a schedule, so run its job body once rather than starting it. The job body is
# the part under test: everything that decides whether an archive is publishable.
docker exec -e POSTGRES_HOST=localhost -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=postgres -e BACKUP_KEEP_DAYS=7 "$PG" sh -c '
        sed -n "/^cat <<.JOB. > \/backup_job.sh$/,/^JOB$/p" /scripts/backup-cron.sh \
            | sed "1d;\$d" > /run_backup.sh
        sh /run_backup.sh
    ' || fail "the backup job failed"

ARCHIVE=$(docker exec "$PG" sh -c 'ls -1 /backups/barako_backup_*.sql.gz 2>/dev/null | head -1')
[ -n "$ARCHIVE" ] || fail "the backup job produced no archive"
SIZE=$(docker exec "$PG" sh -c "wc -c < '$ARCHIVE'")
echo "archive $ARCHIVE ($SIZE bytes)"
[ "$SIZE" -gt 1000 ] || fail "the archive is only $SIZE bytes, which is the empty-gzip failure this script exists to catch"

step "destroying the database"
# A drop rather than a truncate. Restoring over a schema that is still there can succeed for the
# wrong reason: whatever the dump failed to carry is already present.
docker exec "$PG" psql -U postgres -d postgres -c "drop database \"$DB\" with (force);" >/dev/null
docker exec "$PG" psql -U postgres -d postgres -c "create database \"$DB\";" >/dev/null
TABLES=$(docker exec "$PG" psql -U postgres -d "$DB" -tAc "select count(*) from information_schema.tables where table_schema='public';")
[ "$TABLES" = "0" ] || fail "the database still has $TABLES tables, so it was not actually destroyed"
echo "gone"

step "restoring the archive"
docker exec "$PG" sh -c "gunzip -c '$ARCHIVE' | psql -U postgres -d '$DB' -v ON_ERROR_STOP=1" >/dev/null \
    || fail "the restore reported an error"

CONTENT_AFTER=$(psql_q "select count(*) from public.mt_doc_contents;")
USERS_AFTER=$(psql_q "select count(*) from public.mt_doc_users;")
[ "$CONTENT_AFTER" = "$CONTENT_BEFORE" ] || fail "content rows: $CONTENT_BEFORE before, $CONTENT_AFTER after"
[ "$USERS_AFTER" = "$USERS_BEFORE" ] || fail "users: $USERS_BEFORE before, $USERS_AFTER after"
echo "$CONTENT_AFTER content rows, $USERS_AFTER users"

step "booting against the restored database"
start_host >"$WORK/second.log" 2>&1 &
HOST_PID=$!
wait_for_health second.log

step "the restored admin can sign in and read content"
TOKEN=$(curl -s -X POST "http://127.0.0.1:${APP_PORT}/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d.get("accessToken") or d.get("token") or "")')
[ -n "$TOKEN" ] || fail "the admin restored from the backup cannot sign in"

# Polled, because the seeder runs in the background after /health starts answering, and this
# assertion is about the restore rather than about startup timing.
#
# The read is of content rather than content types. The seeder writes its demo content type as a
# Models.ContentType, which is a dead model nothing but the seeder touches; the API's content types
# are ContentTypeDefinition, in a different table, and a freshly seeded database has none. Asserting
# on content types would have failed here for a reason that has nothing to do with backups.
for _ in $(seq 1 30); do
    SERVED=$(curl -s "http://127.0.0.1:${APP_PORT}/api/contents?pageSize=100" -H "Authorization: Bearer $TOKEN" \
        | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d.get("totalItems", 0))' 2>/dev/null || echo 0)
    [ "${SERVED:-0}" -gt 0 ] && break
    sleep 2
done
if [ "${SERVED:-0}" -lt "$CONTENT_BEFORE" ]; then
    echo "--- document tables after restore ---" >&2
    docker exec "$PG" psql -U postgres -d "$DB" -c "
        select relname, n_live_tup from pg_stat_user_tables where relname like 'mt_doc_%' order by relname;" >&2
    echo "--- host log ---" >&2
    tail -20 "$WORK/second.log" >&2
    fail "the restored database serves $SERVED content items, expected $CONTENT_BEFORE"
fi
echo "signed in, $SERVED content items served"

printf '\nA backup taken by scripts/backup-cron.sh restores into an empty database and the app serves from it.\n'

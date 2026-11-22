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
#   1. stand up Postgres, create the app role with scripts/db-app-role.sql (the owner job), and boot
#      the app as that role with database enforcement on, so there is a real schema with content
#   2. take a backup with the same scripts/backup-cron.sh the deployments run, not a bare pg_dump
#   3. destroy the database completely, and the app role with it, as on a fresh volume
#   4. run the owner job again, then restore the archive as the app role, the way
#      docs/backup-and-restore.md says: the CREATE EXTENSION and COMMENT ON EXTENSION lines are
#      dropped from the stream and the dump is loaded with PGOPTIONS='-c role=<app role>'
#   5. assert the restore printed no "does not exist" or "permission denied", every restored object
#      is owned by the app role, and the policies and forced row level security match the ones
#      from before the dump
#   6. boot the app against the restored database, as the app role, and assert the content is there
#
# Step 3 is a drop, not a truncate. Restoring over a database that still has its schema can succeed
# for the wrong reason: the objects the dump failed to carry are already present.
#
# Usage: scripts/restore-check.sh

set -euo pipefail

. "$(dirname "$0")/lib-ports.sh"

PG="restore-check-pg"
# Empty means whoever binds the port chooses it: Docker for Postgres, the kernel for the host. A
# value set by the caller is used as given. See lib-ports.sh for why there is no default.
PG_PORT="${PG_PORT:-}"
APP_PORT="${APP_PORT:-}"
APP_BIND="${APP_PORT:-0}"
DB="barako_restore_check"
JWT_KEY='restore-check-key-that-is-at-least-32-chars-long'
ADMIN_PASSWORD='RestoreCheck!123'
APP_ROLE="barako_app"
# Not the owner password, which db-app-role.sql refuses to share.
APP_PASSWORD='restore-check-app-not-an-example'
WORK="$(mktemp -d)"
# Drops CREATE EXTENSION and COMMENT ON EXTENSION lines, and leaves COPY data blocks alone.
EXT_FILTER='c { print; if ($0 == "\\.") c = 0; next } /^COPY .* FROM stdin;$/ { c = 1 } /^(CREATE|COMMENT ON) EXTENSION / { next } { print }'

cleanup() {
    if [ -n "${HOST_PID:-}" ]; then kill "$HOST_PID" 2>/dev/null || true; wait "$HOST_PID" 2>/dev/null || true; fi
    remove_started "$WORK/pg.cid"
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

# Only a port the caller asked for can be checked ahead of time. One the system chooses does not
# exist yet, and cannot be taken by anything else once it does.
for port in $PG_PORT $APP_PORT; do
    if port_in_use "$port"; then
        fail "port $port was asked for and is held by a listener this run did not start; something else would answer the checks below"
    fi
done

start_host() {
    # exec so the recorded PID is dotnet itself. Backgrounding the function without it makes $! the
    # wrapping subshell, and killing that leaves the host holding the port, which then answers the
    # health check for the next boot and every assertion after it describes the wrong process.
    exec env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
        ASPNETCORE_ENVIRONMENT=Production \
        AllowedHosts="localhost;127.0.0.1" \
        ASPNETCORE_URLS="http://127.0.0.1:${APP_BIND}" \
        "$LISTEN_LOG_ENV" \
        ConnectionStrings__DefaultConnection="$CONN" \
        JWT__Key="$JWT_KEY" \
        InitialAdmin__Username=admin \
        InitialAdmin__Password="$ADMIN_PASSWORD" \
        Kubernetes__Enabled=false \
        Tenancy__DatabaseEnforcement=true \
        Seed__DemoContent=true \
        dotnet exec "$WORK/publish/barakoCMS.dll" "$@"
}

wait_for_health() {
    # Each boot binds again, so the port is read again, from the log of the process just started.
    APP_PORT=$(listen_port "$WORK/$1" "$HOST_PID") || { cat "$WORK/$1" >&2; fail "the host this run started is not listening"; }
    for _ in $(seq 1 60); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${APP_PORT}/health" || true)" = "200" ]; then
            kill -0 "$HOST_PID" 2>/dev/null \
                || fail "something answered /health on port $APP_PORT but the host this run started is gone, so every check below would describe another process"
            return 0
        fi
        kill -0 "$HOST_PID" 2>/dev/null || { cat "$WORK/$1" >&2; fail "the host exited during startup"; }
        sleep 2
    done
    cat "$WORK/$1" >&2
    fail "the host never became healthy"
}

psql_q() { docker exec "$PG" psql -U postgres -d "$DB" -tAc "$1"; }

# The owner job: the same script and inputs the postgres-extensions service runs, against $DB.
run_owner_job() {
    docker cp scripts/db-app-role.sql "$PG:/db-app-role.sql" >/dev/null
    docker exec -e DB_APP_USER="$APP_ROLE" -e DB_APP_PASSWORD="$APP_PASSWORD" -e PGPASSWORD=barako-ci-not-an-example \
        "$PG" psql -U postgres -d "$DB" -X -v ON_ERROR_STOP=1 -q -f /db-app-role.sql >/dev/null \
        || fail "the owner job (scripts/db-app-role.sql) failed"
}

# What row level security looks like in $DB, for the before and after comparison.
security_listing() {
    psql_q "select schemaname, tablename, policyname, permissive, roles, cmd, qual, with_check
            from pg_policies order by 1, 2, 3;"
    echo "--- forced"
    psql_q "select c.relname, c.relrowsecurity, c.relforcerowsecurity from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public' and c.relkind in ('r', 'p') order by 1;"
}

step "building the host"
dotnet publish barakoCMS/barakoCMS.csproj -c Release -o "$WORK/publish" --nologo -v q -clp:ErrorsOnly -p:RestoreLockedMode=true

step "starting postgres"
PG_ID=$(docker run -d --cidfile "$WORK/pg.cid" --name "$PG" -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=barako-ci-not-an-example -p "$(publish_spec "$PG_PORT" 5432)" postgres:16-alpine 2>"$WORK/docker-run.err") \
    || fail "$(publish_failure "$WORK/docker-run.err" "$PG_PORT" postgres)"
PG_PORT=$(published_port "$PG_ID" 5432) || fail "cannot tell which host port postgres was published on"
# The API connects as the app role, never the owner.
CONN="Host=127.0.0.1;Port=${PG_PORT};Database=${DB};Username=${APP_ROLE};Password=${APP_PASSWORD}"
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

step "the owner job, before the first boot"
run_owner_job

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

security_listing >"$WORK/security-before.txt"
[ -n "$(psql_q "select 1 from pg_policies limit 1;")" ] \
    || fail "no row level security policies exist with enforcement on, so the comparison after the restore would prove nothing"
[ -n "$(psql_q "select 1 from pg_class where relforcerowsecurity limit 1;")" ] \
    || fail "no table has forced row level security with enforcement on, so the comparison after the restore would prove nothing"
echo "$(grep -c . "$WORK/security-before.txt") lines of policies and forced tables recorded"

kill "$HOST_PID"; wait "$HOST_PID" 2>/dev/null || true; HOST_PID=""
# Wait for the port, not just the process: the assertions after the restore are worthless if the
# next boot silently fails to bind and the old host answers them.
#
# Only when the caller fixed the port. Otherwise the next boot binds a new one and reads it from its
# own log, so it cannot mistake the old host for itself.
if [ "$APP_BIND" != 0 ]; then
    for _ in $(seq 1 30); do port_in_use "$APP_PORT" || break; sleep 1; done
    port_in_use "$APP_PORT" && fail "the first host is still holding port $APP_PORT"
fi


step "taking a backup with the script the deployments run"
docker exec -e POSTGRES_HOST=localhost -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=barako-ci-not-an-example -e BACKUP_CRON_SCHEDULE="0 2 * * *" -e BACKUP_KEEP_DAYS=7 \
    "$PG" sh -c 'mkdir -p /backups /scripts' >/dev/null
docker cp scripts/backup-cron.sh "$PG:/scripts/backup-cron.sh" >/dev/null

# The daemon loops on a schedule, so run its job body once rather than starting it. The job body is
# the part under test: everything that decides whether an archive is publishable.
docker exec -e POSTGRES_HOST=localhost -e POSTGRES_DB="$DB" -e POSTGRES_USER=postgres \
    -e POSTGRES_PASSWORD=barako-ci-not-an-example -e BACKUP_KEEP_DAYS=7 "$PG" sh -c '
        sed -n "/^cat <<.JOB. > \/tmp\/backup_job.sh$/,/^JOB$/p" /scripts/backup-cron.sh \
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
# wrong reason: whatever the dump failed to carry is already present. The app role goes too, so the
# restore starts as it does on a fresh volume where the role does not exist yet.
docker exec "$PG" psql -U postgres -d postgres -c "drop database \"$DB\" with (force);" >/dev/null
docker exec "$PG" psql -U postgres -d postgres -c "create database \"$DB\";" >/dev/null
docker exec "$PG" psql -U postgres -d postgres -c "drop role \"$APP_ROLE\";" >/dev/null \
    || fail "could not drop the app role"
TABLES=$(docker exec "$PG" psql -U postgres -d "$DB" -tAc "select count(*) from information_schema.tables where table_schema='public';")
[ "$TABLES" = "0" ] || fail "the database still has $TABLES tables, so it was not actually destroyed"
[ -z "$(docker exec "$PG" psql -U postgres -d postgres -tAc "select 1 from pg_roles where rolname = '$APP_ROLE'")" ] \
    || fail "the app role still exists, so this is not a fresh start"
echo "gone"

step "the owner job on the empty database"
run_owner_job

step "restoring the archive as the app role"
# The archive is read in the container and filtered and loaded from here, the way the restore
# block in docs/backup-and-restore.md does it. ON_ERROR_STOP stops at the first error, and the
# output is read as well, so a message that is not an error still fails the run.
docker exec "$PG" sh -c "gunzip -c '$ARCHIVE'" | awk "$EXT_FILTER" \
    | docker exec -i -e PGOPTIONS="-c role=$APP_ROLE" "$PG" psql -U postgres -d "$DB" -v ON_ERROR_STOP=1 \
    >"$WORK/restore.out" 2>&1 || { tail -20 "$WORK/restore.out" >&2; fail "the restore reported an error"; }
if grep -E 'does not exist|permission denied' "$WORK/restore.out" >&2; then
    fail "the restore output holds a missing role, object or a permission error"
fi

step "every restored object belongs to the app role"
# Members of an extension belong to whoever created the extension, so they are left out.
NOT_APP_RELATIONS=$(psql_q "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace
    where n.nspname = 'public' and c.relkind in ('r', 'p', 'v', 'm', 'S', 'f')
      and pg_get_userbyid(c.relowner) <> '$APP_ROLE'
      and not exists (select 1 from pg_depend d where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.deptype = 'e');")
NOT_APP_FUNCTIONS=$(psql_q "select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public' and pg_get_userbyid(p.proowner) <> '$APP_ROLE'
      and not exists (select 1 from pg_depend d where d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e');")
APP_TABLES=$(psql_q "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace
    where n.nspname = 'public' and c.relkind = 'r' and pg_get_userbyid(c.relowner) = '$APP_ROLE';")
[ "$APP_TABLES" -gt 0 ] || fail "no restored table is owned by $APP_ROLE"
[ "$NOT_APP_RELATIONS" = 0 ] || fail "$NOT_APP_RELATIONS restored relations in public are not owned by $APP_ROLE"
[ "$NOT_APP_FUNCTIONS" = 0 ] || fail "$NOT_APP_FUNCTIONS restored functions in public are not owned by $APP_ROLE"
echo "$APP_TABLES tables owned by $APP_ROLE, nothing else owned by anyone else"

step "policies and forced row level security match the ones from before the dump"
security_listing >"$WORK/security-after.txt"
diff -u "$WORK/security-before.txt" "$WORK/security-after.txt" >&2 \
    || fail "the policies or forced row level security differ after the restore"
echo "same"

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

printf '\nA backup taken by scripts/backup-cron.sh restores as the app role into an empty database and the app serves from it.\n'

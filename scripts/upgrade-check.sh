#!/usr/bin/env bash
#
# Proves the 3.x to 4.0 upgrade, and the rollback, against a database that has data in it.
#
# Nothing in the test suite covers this: IntegrationTestFixture forces Development, so the suite
# always runs CreateOrUpdate against a fresh database, and production runs CreateOnly against one
# that already exists. That gap is what issue #277 is about, and this script is what closes it.
#
# The sequence, which is also the documented upgrade and rollback procedure:
#
#   1. stand up a database with the released FROM_VERSION and put real content in it
#   2. db-assert must FAIL on both hosts, because 4.0's schema does not match a 3.x database
#   3. apply the reviewed core migrations, migrations/4.0.0/3.x-to-4.0.sql,
#      migrations/4.2.0/user-normalized-identity.sql, migrations/4.3.0/collection-syncs.sql
#      and migrations/4.3.0/marten-9-37-event-store-columns.sql
#   4. db-assert must PASS on the core host, so those files are exactly what core needs
#   5. apply the module migrations, migrations/4.2.0/stored-files-parent-index.sql and
#      migrations/4.2.0/forms-public-forms.sql
#   6. db-assert must PASS on the Suite host, so nothing any module registers is left outstanding
#   7. the Suite boots in Production mode, module schema preflight included, and serves
#   8. an event appends to a stream that already existed, and the projection daemon resumes from
#      its stored progression rather than restarting from zero
#   9. 4.0 stops, and the rollback files are applied newest first:
#      migrations/4.3.0/rollback-collection-syncs.sql,
#      migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql,
#      migrations/4.2.0/rollback-user-normalized-identity.sql and
#      migrations/4.0.0/rollback-to-3.x.sql
#  10. FROM_VERSION boots again against the rolled-back database and still serves the record 4.0
#      wrote to, with every event still on its stream
#
# Step 2 is asserted rather than skipped on purpose. If a future change makes the migration
# unnecessary, this fails and someone finds out deliberately instead of shipping a stale file.
# Steps 5 to 7 run the Suite because that is what ghcr.io/baryodev/barako-cms runs. This gate used to
# run only the core host, which loads no module, so it went green for all of 4.0 while the Suite
# refused to start on the first upgraded database it met: Files wanted an index on
# mt_doc_stored_files that CreateOnly never adds (#661). The core host stays in steps 2 and 4
# because the decaf image runs it, and because it pins the core file to core's objects alone.
#
# Step 10 exists because #604 shipped a rollback file nothing had ever executed: it did not even
# parse. Running it here means a future edit that breaks it fails this job instead of an operator
# mid-incident.
#
# Usage: scripts/upgrade-check.sh          (FROM_VERSION defaults to the last 3.x release)

set -euo pipefail

FROM_VERSION="${FROM_VERSION:-3.21.0}"
IMAGE="${IMAGE:-ghcr.io/baryodev/barako-cms:${FROM_VERSION}}"
NETWORK="${NETWORK:-barako-upgrade-check}"
PG="${PG:-upgrade-check-pg}"
OLD="${OLD:-upgrade-check-old}"
PG_PORT="${PG_PORT:-55433}"
NEW_PORT="${NEW_PORT:-58090}"
OLD_PORT="${OLD_PORT:-58091}"
ADMIN_PASSWORD='UpgradeCheck!123'
JWT_KEY='upgrade-check-key-that-is-at-least-32-chars-long'
WORK="$(mktemp -d)"
CONN="Host=127.0.0.1;Port=${PG_PORT};Database=barako_cms;Username=postgres;Password=postgres"

cleanup() {
    if [ -n "${HOST_PID:-}" ]; then kill "$HOST_PID" 2>/dev/null || true; wait "$HOST_PID" 2>/dev/null || true; fi
    docker rm -f "$PG" "$OLD" >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

# $1 is the host dll, core or Suite; the rest are its arguments.
run_host() {
    local dll="$1"
    shift
    # Explicit environment, not an inherited one: a stray DATABASE_URL in the shell would point the
    # host somewhere other than the database under test and every check below would pass wrongly.
    #
    # HOST_EXEC=exec is for the backgrounded boot. `run_suite &` forks a subshell and $! names it,
    # not dotnet, so killing $! left the 4.0 host running through the rollback and after the script.
    ${HOST_EXEC:-} env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
        ASPNETCORE_ENVIRONMENT=Production \
        ASPNETCORE_URLS="http://127.0.0.1:${NEW_PORT}" \
        ConnectionStrings__DefaultConnection="$CONN" \
        JWT__Key="$JWT_KEY" \
        SKIP_SEEDER=true \
        Kubernetes__Enabled=false \
        dotnet exec "$dll" "$@"
}

run_core() { run_host "$WORK/core/barakoCMS.dll" "$@"; }
run_suite() { run_host "$WORK/suite/BarakoCMS.Suite.dll" "$@"; }

psql_q() { docker exec "$PG" psql -U postgres -d barako_cms -tAc "$1"; }

# A port already in use is the one failure that makes every check below pass for the wrong reason:
# the health probe reaches whatever is listening, and the assertions then describe someone else's
# process. Refuse to start rather than produce a green run about the wrong host.
#
# lsof is not on every runner, and `if lsof ...` on a missing binary is false, which reads as "port
# free" and fails open. Pick a tool that exists, and say so when neither does.
if command -v lsof >/dev/null 2>&1; then
    port_in_use() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
elif command -v ss >/dev/null 2>&1; then
    port_in_use() { ss -ltn "sport = :$1" 2>/dev/null | grep -q LISTEN; }
else
    echo "note: neither lsof nor ss is available, so the port check below is skipped" >&2
    port_in_use() { return 1; }
fi

for port in "$PG_PORT" "$NEW_PORT" "$OLD_PORT"; do
    if port_in_use "$port"; then
        fail "port $port is already in use. Something else would answer the health checks below and this run would pass without testing anything."
    fi
done

step "building 4.0 from the working tree, the Suite and the core host"
dotnet publish BarakoCMS.Suite/BarakoCMS.Suite.csproj -c Release -o "$WORK/suite" --nologo -v q -clp:ErrorsOnly -p:RestoreLockedMode=true -nodeReuse:false
dotnet publish barakoCMS/barakoCMS.csproj -c Release -o "$WORK/core" --nologo -v q -clp:ErrorsOnly -p:RestoreLockedMode=true -nodeReuse:false

step "starting postgres"
docker network create "$NETWORK" >/dev/null 2>&1 || true
docker run -d --name "$PG" --network "$NETWORK" \
    -e POSTGRES_DB=barako_cms -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
# pg_isready over the Unix socket is satisfied by the WRONG server. The postgres image boots a
# temporary initdb instance on the socket only, creates the database, shuts it down, and then starts
# the real one listening on TCP. So a socket check succeeds during bootstrap, the wait breaks early,
# and the verification a moment later lands in the shutdown window and reports that postgres never
# became ready. The whole failure takes four seconds out of a two minute budget.
#
# -h 127.0.0.1 forces TCP, which only the real server accepts. Same loop, right server.
for _ in $(seq 1 60); do docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break; sleep 2; done
if ! docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; then
    echo "--- postgres container ---" >&2
    docker logs "$PG" 2>&1 | tail -30 >&2
    docker inspect "$PG" --format 'state={{.State.Status}} exit={{.State.ExitCode}}' >&2 || true
    fail "postgres never became ready"
fi

step "creating a ${FROM_VERSION} database with data in it"
docker run -d --name "$OLD" --network "$NETWORK" \
    -e ConnectionStrings__DefaultConnection="Host=${PG};Database=barako_cms;Username=postgres;Password=postgres" \
    -e JWT__Key="$JWT_KEY" \
    -e InitialAdmin__Username=admin -e InitialAdmin__Password="$ADMIN_PASSWORD" \
    -e Kubernetes__Enabled=false \
    -p "${OLD_PORT}:8080" "$IMAGE" >/dev/null

OLD_URL="http://127.0.0.1:${OLD_PORT}"
for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health" || true)" = "200" ] && break
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health")" = "200" ] || fail "${FROM_VERSION} never became healthy"

TOKEN=$(curl -s -X POST "$OLD_URL/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('accessToken') or d.get('token') or '')")
[ -n "$TOKEN" ] || fail "could not sign in to ${FROM_VERSION}"

CONTENT_ID=$(curl -s -X POST "$OLD_URL/api/contents" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -d '{"contentType":"AttendanceRecord","data":{"FirstName":"Upgrade","LastName":"Probe"}}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
[ -n "$CONTENT_ID" ] || fail "could not create content on ${FROM_VERSION}"

# The field is newStatus. Sending "status" binds nothing: the enum defaults to 0, which is Draft,
# and 3.x accepted that silently rather than refusing it.
curl -s -X PUT "$OLD_URL/api/contents/$CONTENT_ID/status" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' -d '{"newStatus":1}' >/dev/null

EVENTS_BEFORE=$(psql_q "select count(*) from mt_events where stream_id = '$CONTENT_ID';")
[ "$EVENTS_BEFORE" -ge 2 ] || fail "expected an event stream from ${FROM_VERSION}, found $EVENTS_BEFORE events"
echo "stream $CONTENT_ID has $EVENTS_BEFORE events"

# The daemon writes its progression asynchronously, so wait for it rather than assuming. Everything
# below compares against this number, and a zero here would make those comparisons meaningless.
for _ in $(seq 1 30); do
    SEEN=$(psql_q "select coalesce(max(last_seq_id), 0) from mt_event_progression where name like '%WorkflowProjection%';")
    [ "${SEEN:-0}" -gt 0 ] && break
    sleep 2
done
[ "${SEEN:-0}" -gt 0 ] \
    || fail "the ${FROM_VERSION} workflow projection never recorded a progression, so there is no 'before' to compare against"

docker stop "$OLD" >/dev/null

# Read AFTER the old container is stopped, not before. The loop above breaks on the first non-zero
# value, and the daemon can flush another one while docker stop is still landing, so a number taken
# there is mid-flight: the comparison below then reported the daemon's own progress as if the
# migration had reset it, and failed a PR whose migration never touches this table. Once the writer
# is gone the number cannot move, which is what makes this a baseline rather than a sample.
PROGRESSION_BEFORE=$(psql_q "select coalesce(max(last_seq_id), 0) from mt_event_progression where name like '%WorkflowProjection%';")
echo "workflow projection progression is $PROGRESSION_BEFORE"

step "db-assert must refuse the un-migrated database, on both hosts"
if run_core db-assert >"$WORK/assert-before-core.log" 2>&1; then
    fail "core db-assert passed against a ${FROM_VERSION} database. The committed migration is stale: regenerate it with db-patch, or delete it if 4.0 no longer needs one."
fi
if run_suite db-assert >"$WORK/assert-before-suite.log" 2>&1; then
    fail "Suite db-assert passed against a ${FROM_VERSION} database, where core alone refuses it"
fi
echo "refused, as it must"

step "applying migrations/4.0.0/3.x-to-4.0.sql"
docker cp migrations/4.0.0/3.x-to-4.0.sql "$PG:/tmp/up.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/up.sql >/dev/null

step "applying migrations/4.2.0/user-normalized-identity.sql"
docker cp migrations/4.2.0/user-normalized-identity.sql "$PG:/tmp/users.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/users.sql >/dev/null

# The collection sync table (#794). Core, not a module, so it has to land before core's assert
# below. CreateOnly would create it on first boot; the file exists so the deploy gate passes
# before the container is replaced rather than reporting the table as outstanding.
step "applying migrations/4.3.0/collection-syncs.sql"
docker cp migrations/4.3.0/collection-syncs.sql "$PG:/tmp/collection-syncs.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/collection-syncs.sql >/dev/null

# Marten 9.37's event store columns. Core, not a module: these are mt_streams and
# mt_event_progression, so they have to land before core's assert below.
step "applying migrations/4.3.0/marten-9-37-event-store-columns.sql"
docker cp migrations/4.3.0/marten-9-37-event-store-columns.sql "$PG:/tmp/marten937.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/marten937.sql >/dev/null

step "the migration left the daemon's progression alone"
PROGRESSION_MIGRATED=$(psql_q "select coalesce(max(last_seq_id), 0) from mt_event_progression where name like '%WorkflowProjection%';")
[ "$PROGRESSION_MIGRATED" = "$PROGRESSION_BEFORE" ] \
    || fail "the migration moved the workflow projection from $PROGRESSION_BEFORE to $PROGRESSION_MIGRATED. A reset here means 4.0 replays every event on first boot, re-firing every workflow email, webhook and task."
echo "still $PROGRESSION_MIGRATED"

step "core db-assert must now pass"
run_core db-assert >"$WORK/assert-after-core.log" 2>&1 || {
    cat "$WORK/assert-after-core.log" >&2
    fail "the core migrations under migrations/ did not bring core's schema up to date. Whatever db-assert lists above needs a file under the release being deployed, and this script needs to apply it."
}
echo "core schema matches"

# CONCURRENTLY, so this file cannot run inside a transaction, and it says so itself.
step "applying migrations/4.2.0/stored-files-parent-index.sql"
docker cp migrations/4.2.0/stored-files-parent-index.sql "$PG:/tmp/modules.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 -f /tmp/modules.sql >/dev/null

step "applying migrations/4.2.0/forms-public-forms.sql"
docker cp migrations/4.2.0/forms-public-forms.sql "$PG:/tmp/forms.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/forms.sql >/dev/null

step "Suite db-assert must now pass, every module included"
run_suite db-assert >"$WORK/assert-after-suite.log" 2>&1 || {
    cat "$WORK/assert-after-suite.log" >&2
    fail "the migrations brought core up to date and left a module behind. The Suite, which the published image runs, would refuse to start on this database. Whatever db-assert lists above needs a migration, and docs/upgrading-to-4.0.md needs to name it."
}
echo "Suite schema matches"

step "booting the 4.0 Suite in Production against the migrated database"
HOST_EXEC=exec run_suite >"$WORK/boot.log" 2>&1 &
HOST_PID=$!
NEW_URL="http://127.0.0.1:${NEW_PORT}"
for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$NEW_URL/health" || true)" = "200" ] && break
    kill -0 "$HOST_PID" 2>/dev/null || { cat "$WORK/boot.log" >&2; fail "4.0 exited during startup"; }
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$NEW_URL/health")" = "200" ] || {
    cat "$WORK/boot.log" >&2; fail "4.0 never became healthy"
}
kill -0 "$HOST_PID" 2>/dev/null || {
    cat "$WORK/boot.log" >&2
    fail "something answered /health but the host we started is gone, so every check below would describe another process"
}

step "the 3.x admin can still sign in"
NEW_TOKEN=$(curl -s -X POST "$NEW_URL/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('accessToken') or d.get('token') or '')")
[ -n "$NEW_TOKEN" ] || fail "the ${FROM_VERSION} admin cannot sign in to 4.0"

step "an event appends to the stream that already existed"
curl -s -X PUT "$NEW_URL/api/contents/$CONTENT_ID/status" -H "Authorization: Bearer $NEW_TOKEN" \
    -H 'Content-Type: application/json' -d '{"newStatus":2}' >/dev/null
EVENTS_AFTER=$(psql_q "select count(*) from mt_events where stream_id = '$CONTENT_ID';")
[ "$EVENTS_AFTER" -gt "$EVENTS_BEFORE" ] \
    || fail "no event appended to the pre-existing stream ($EVENTS_BEFORE then $EVENTS_AFTER)"
echo "$EVENTS_BEFORE then $EVENTS_AFTER events"

step "the projection daemon picked up where it left off"
# Polled, not slept: the daemon takes the HotCold advisory lock and catches up on its own schedule,
# and a fixed sleep turns a tenancy assertion into a timing one.
for _ in $(seq 1 30); do
    PROGRESSION_AFTER=$(psql_q "select coalesce(max(last_seq_id), 0) from mt_event_progression where name like '%WorkflowProjection%';")
    [ "${PROGRESSION_AFTER:-0}" -gt "$PROGRESSION_BEFORE" ] && break
    sleep 2
done
if [ "${PROGRESSION_AFTER:-0}" -le "$PROGRESSION_BEFORE" ]; then
    echo "--- host log ---" >&2
    tail -60 "$WORK/boot.log" >&2
    fail "the workflow projection sat at $PROGRESSION_AFTER after the new event, having been at $PROGRESSION_BEFORE before the upgrade; the daemon did not resume"
fi
echo "$PROGRESSION_BEFORE then $PROGRESSION_AFTER"

# The rollback. docs/upgrading-to-4.0.md tells an operator to stop 4.0 first, so this does the same:
# kill the host process, then apply rollback-to-3.x.sql to the same postgres container the forward
# migration ran against, then boot the FROM_VERSION image again to prove the documented way back
# actually lands on a schema that version recognises. $OLD is reused rather than started fresh: it
# already has the admin user and the InitialAdmin env it was created with, so a second `docker run`
# would either collide on the name or seed a second admin, and neither proves anything a restart of
# the same container does not.
step "stopping 4.0 before the rollback"
kill "$HOST_PID" 2>/dev/null || true
wait "$HOST_PID" 2>/dev/null || true
HOST_PID=""

# Newest first, the reverse of the order the forward files ran in. FROM_VERSION asserts its own
# schema and refuses to boot while anything it does not declare is still there, whether that is a
# table or a column. The two 4.3.0 files touch different objects, so their order between
# themselves does not matter; both have to run before the older rollbacks.
step "applying migrations/4.3.0/rollback-collection-syncs.sql"
docker cp migrations/4.3.0/rollback-collection-syncs.sql "$PG:/tmp/collection-syncs-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/collection-syncs-down.sql >/dev/null

step "applying migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql"
docker cp migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql "$PG:/tmp/marten937-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/marten937-down.sql >/dev/null

step "applying migrations/4.2.0/rollback-user-normalized-identity.sql"
docker cp migrations/4.2.0/rollback-user-normalized-identity.sql "$PG:/tmp/users-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/users-down.sql >/dev/null

step "applying migrations/4.0.0/rollback-to-3.x.sql"
docker cp migrations/4.0.0/rollback-to-3.x.sql "$PG:/tmp/down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/down.sql >/dev/null

step "booting ${FROM_VERSION} again against the rolled-back database"
docker start "$OLD" >/dev/null
for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health" || true)" = "200" ] && break
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health")" = "200" ] || {
    echo "--- ${FROM_VERSION} container after rollback ---" >&2
    docker logs "$OLD" 2>&1 | tail -30 >&2
    fail "${FROM_VERSION} did not come back up against the rolled-back database. The documented rollback did not land on a schema ${FROM_VERSION} recognises."
}
echo "${FROM_VERSION} is healthy again"

step "${FROM_VERSION} still serves the record 4.0 wrote to, after the rollback"
ROLLBACK_TOKEN=$(curl -s -X POST "$OLD_URL/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('accessToken') or d.get('token') or '')")
[ -n "$ROLLBACK_TOKEN" ] || fail "the admin cannot sign in to ${FROM_VERSION} after rollback"

ROLLBACK_FIRST_NAME=$(curl -s "$OLD_URL/api/contents/$CONTENT_ID" -H "Authorization: Bearer $ROLLBACK_TOKEN" \
    | python3 -c "import sys,json; print(json.load(sys.stdin).get('data',{}).get('FirstName',''))")
[ "$ROLLBACK_FIRST_NAME" = "Upgrade" ] \
    || fail "expected content $CONTENT_ID to still read back FirstName 'Upgrade' through ${FROM_VERSION} after rollback, got '$ROLLBACK_FIRST_NAME'"

# The status change was made under 4.0 (newStatus 2, Archived). It lives on the document itself, not
# only in the event stream, and rollback does not touch mt_doc_contents rows, so it must still be 2.
# Read straight from the column rather than through the API: the JSON enum name is a 4.0-side detail
# this test has no need to depend on.
ROLLBACK_STATUS=$(psql_q "select data ->> 'Status' from mt_doc_contents where id = '$CONTENT_ID';")
[ "$ROLLBACK_STATUS" = "2" ] \
    || fail "expected content $CONTENT_ID to still have Status 2 (set by 4.0) after rollback, got '$ROLLBACK_STATUS'"

EVENTS_ROLLED_BACK=$(psql_q "select count(*) from mt_events where stream_id = '$CONTENT_ID';")
[ "$EVENTS_ROLLED_BACK" = "$EVENTS_AFTER" ] \
    || fail "expected $EVENTS_AFTER events on stream $CONTENT_ID after rollback, found $EVENTS_ROLLED_BACK. A rollback must not lose events."
echo "${FROM_VERSION} reads it back: FirstName $ROLLBACK_FIRST_NAME, Status $ROLLBACK_STATUS, $EVENTS_ROLLED_BACK events on the stream"

printf '\nThe %s to 4.0 upgrade works on the Suite host, with migrations/4.0.0/3.x-to-4.0.sql, migrations/4.2.0/user-normalized-identity.sql, migrations/4.3.0/collection-syncs.sql, migrations/4.3.0/marten-9-37-event-store-columns.sql, migrations/4.2.0/stored-files-parent-index.sql and migrations/4.2.0/forms-public-forms.sql applied first, and rolls back cleanly with migrations/4.3.0/rollback-collection-syncs.sql, migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql, migrations/4.2.0/rollback-user-normalized-identity.sql and migrations/4.0.0/rollback-to-3.x.sql.\n' "$FROM_VERSION"

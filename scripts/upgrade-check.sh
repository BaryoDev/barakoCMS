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
#      migrations/4.2.0/user-normalized-identity.sql, migrations/4.2.0/site-share-links.sql,
#      migrations/4.3.0/collection-syncs.sql, migrations/4.3.0/marten-9-37-event-store-columns.sql,
#      migrations/4.4.0/marten-9-38-quick-append-events.sql,
#      migrations/4.5.0/refresh-token-hash-index.sql and
#      migrations/4.6.0/sensitivity-by-capability.sql, after which the HR role FROM_VERSION seeded
#      holds view_sensitive
#   4. db-assert must PASS on the core host, so those files are exactly what core needs
#   5. apply the module migrations, migrations/4.2.0/stored-files-parent-index.sql,
#      migrations/4.2.0/forms-public-forms.sql, migrations/4.5.0/email-sent-emails.sql and
#      migrations/4.6.0/external-auth-identities.sql
#   6. db-assert must PASS on the Suite host, so nothing any module registers is left outstanding
#   7. the Suite boots in Production mode, module schema preflight included, and serves
#   8. an event appends to a stream that already existed, and the projection daemon resumes from
#      its stored progression rather than restarting from zero
#   9. the new build stops, and the rollback files are applied newest first:
#      migrations/4.6.0/rollback-external-auth-identities.sql,
#      migrations/4.6.0/rollback-sensitivity-by-capability.sql,
#      migrations/4.5.0/rollback-email-sent-emails.sql,
#      migrations/4.5.0/rollback-refresh-token-hash-index.sql,
#      migrations/4.4.0/rollback-marten-9-38-quick-append-events.sql,
#      migrations/4.3.0/rollback-collection-syncs.sql,
#      migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql,
#      migrations/4.2.0/rollback-site-share-links.sql,
#      migrations/4.2.0/rollback-user-normalized-identity.sql and
#      migrations/4.0.0/rollback-to-3.x.sql
#  10. FROM_VERSION boots again against the rolled-back database and still serves the record the
#      new build wrote to, with every event still on its stream
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
# A 4.0 or 4.1 start (FROM_VERSION=4.1.0) runs the same sequence without the two 4.0.0 files. Such
# a database is already past the 4.0.0 file and nobody runs it a second time, so anything a later
# release folded into that file reaches it only through a file of its own. That is how
# mt_doc_site_share_links went missing from every upgraded 4.0 and 4.1 install while this job,
# starting from 3.x, stayed green (#1007). Its rollback stops at FROM_VERSION and boots that image
# again.
#
# Usage: scripts/upgrade-check.sh                       (FROM_VERSION defaults to the last 3.x release)
#        FROM_VERSION=4.1.0 scripts/upgrade-check.sh    (a database 4.0 or 4.1 created)

set -euo pipefail

. "$(dirname "$0")/lib-ports.sh"
. "$(dirname "$0")/lib-upgrade-data.sh"

FROM_VERSION="${FROM_VERSION:-3.21.0}"
IMAGE="${IMAGE:-ghcr.io/baryodev/barako-cms:${FROM_VERSION}}"
NETWORK="${NETWORK:-barako-upgrade-check}"
PG="${PG:-upgrade-check-pg}"
OLD="${OLD:-upgrade-check-old}"
# Empty means whoever binds the port chooses it: Docker for Postgres and the FROM_VERSION container,
# the kernel for the new host. A value set by the caller is used as given. See lib-ports.sh for why
# there is no default.
PG_PORT="${PG_PORT:-}"
NEW_PORT="${NEW_PORT:-}"
NEW_BIND="${NEW_PORT:-0}"
OLD_PORT="${OLD_PORT:-}"
OLD_PUBLISH="$OLD_PORT"
ADMIN_PASSWORD='UpgradeCheck!123'
JWT_KEY='upgrade-check-key-that-is-at-least-32-chars-long'
WORK="$(mktemp -d)"

cleanup() {
    if [ -n "${HOST_PID:-}" ]; then kill "$HOST_PID" 2>/dev/null || true; wait "$HOST_PID" 2>/dev/null || true; fi
    remove_started "$WORK/pg.cid" "$WORK/old.cid"
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

# Which files a database still needs depends on what created it. 4.0 and 4.1 declare the same
# objects, so a database either one booted takes the same files. Anything else stops here rather
# than guessing: a 4.2 or later start would have to leave out its own release's files.
case "$FROM_VERSION" in
    3.*) FROM_3X=1 ;;
    4.0.*|4.1.*) FROM_3X=0 ;;
    *) fail "FROM_VERSION=${FROM_VERSION} is not a start this script knows. Use a 3.x, 4.0.x or 4.1.x release." ;;
esac

# $1 is the host dll, core or Suite; the rest are its arguments.
run_host() {
    local dll="$1"
    shift
    # Explicit environment, not an inherited one: a stray DATABASE_URL in the shell would point the
    # host somewhere other than the database under test and every check below would pass wrongly.
    #
    # HOST_EXEC=exec is for the backgrounded boot. `run_suite &` forks a subshell and $! names it,
    # not dotnet, so killing $! left the new host running through the rollback and after the script.
    ${HOST_EXEC:-} env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" \
        ASPNETCORE_ENVIRONMENT=Production \
        ASPNETCORE_URLS="http://127.0.0.1:${NEW_BIND}" \
        "$LISTEN_LOG_ENV" \
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

# Only a port the caller asked for can be checked ahead of time. One the system chooses does not
# exist yet, and cannot be taken by anything else once it does.
for port in $PG_PORT $NEW_PORT $OLD_PORT; do
    if port_in_use "$port"; then
        fail "port $port was asked for and is held by a listener this run did not start. Something else would answer the health checks below and this run would pass without testing anything."
    fi
done

# The FROM_VERSION container is stopped for the upgrade and started again after the rollback. Docker
# gives a container a new host port each time it starts unless the caller fixed one, so this is read
# after every start rather than once.
old_url() {
    OLD_PORT=$(published_port "$OLD_ID" 8080) || {
        echo "--- ${FROM_VERSION} container ---" >&2
        docker logs "$OLD_ID" 2>&1 | tail -30 >&2
        fail "${FROM_VERSION} has no published port, so the container this run started is not running"
    }
    OLD_URL="http://127.0.0.1:${OLD_PORT}"
}

# The published port belongs to the container only while it runs. Without this, a container that
# died after answering would leave the port to whatever took it next.
old_is_running() {
    [ "$(docker inspect --format '{{.State.Running}}' "$OLD_ID" 2>/dev/null)" = "true" ] \
        || fail "something answered /health on port $OLD_PORT but the ${FROM_VERSION} container this run started is not running, so every check below would describe another process"
}

step "building the working tree, the Suite and the core host"
dotnet publish BarakoCMS.Suite/BarakoCMS.Suite.csproj -c Release -o "$WORK/suite" --nologo -v q -clp:ErrorsOnly -p:RestoreLockedMode=true -nodeReuse:false
dotnet publish barakoCMS/barakoCMS.csproj -c Release -o "$WORK/core" --nologo -v q -clp:ErrorsOnly -p:RestoreLockedMode=true -nodeReuse:false

step "starting postgres"
docker network create "$NETWORK" >/dev/null 2>&1 || true
PG_ID=$(docker run -d --cidfile "$WORK/pg.cid" --name "$PG" --network "$NETWORK" \
    -e POSTGRES_DB=barako_cms -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    -p "$(publish_spec "$PG_PORT" 5432)" postgres:16-alpine 2>"$WORK/docker-run.err") \
    || fail "$(publish_failure "$WORK/docker-run.err" "$PG_PORT" postgres)"
PG_PORT=$(published_port "$PG_ID" 5432) || fail "cannot tell which host port postgres was published on"
CONN="Host=127.0.0.1;Port=${PG_PORT};Database=barako_cms;Username=postgres;Password=postgres"
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
OLD_ID=$(docker run -d --cidfile "$WORK/old.cid" --name "$OLD" --network "$NETWORK" \
    -e ConnectionStrings__DefaultConnection="Host=${PG};Database=barako_cms;Username=postgres;Password=postgres" \
    -e JWT__Key="$JWT_KEY" \
    -e InitialAdmin__Username=admin -e InitialAdmin__Password="$ADMIN_PASSWORD" \
    -e Kubernetes__Enabled=false \
    -p "$(publish_spec "$OLD_PUBLISH" 8080)" "$IMAGE" 2>"$WORK/docker-run.err") \
    || fail "$(publish_failure "$WORK/docker-run.err" "$OLD_PUBLISH" "$IMAGE")"

old_url
for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health" || true)" = "200" ] && break
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health")" = "200" ] || fail "${FROM_VERSION} never became healthy"
old_is_running

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
    fail "core db-assert passed against a ${FROM_VERSION} database. The committed migration is stale: regenerate it with db-patch, or delete it if the working tree no longer needs one."
fi
if run_suite db-assert >"$WORK/assert-before-suite.log" 2>&1; then
    fail "Suite db-assert passed against a ${FROM_VERSION} database, where core alone refuses it"
fi
echo "refused, as it must"

if [ "$FROM_3X" = 1 ]; then
    step "applying migrations/4.0.0/3.x-to-4.0.sql"
    docker cp migrations/4.0.0/3.x-to-4.0.sql "$PG:/tmp/up.sql"
    docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/up.sql >/dev/null
fi

step "applying migrations/4.2.0/user-normalized-identity.sql"
docker cp migrations/4.2.0/user-normalized-identity.sql "$PG:/tmp/users.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/users.sql >/dev/null

# The share links table (#841, #1007). The 4.0.0 file creates it too, so on a 3.x start this is the
# second run of the same statements and shows the file is harmless there. On a 4.x start it is the
# only thing that creates the table, and the check says so: a 4.x database that already has it means
# this file has stopped doing anything and someone should find out.
if [ "$FROM_3X" = 0 ]; then
    [ "$(psql_q "select to_regclass('public.mt_doc_site_share_links') is null;")" = "t" ] \
        || fail "a ${FROM_VERSION} database already has mt_doc_site_share_links, so migrations/4.2.0/site-share-links.sql proves nothing on this start"
fi
step "applying migrations/4.2.0/site-share-links.sql"
docker cp migrations/4.2.0/site-share-links.sql "$PG:/tmp/share-links.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/share-links.sql >/dev/null

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

step "applying migrations/4.4.0/marten-9-38-quick-append-events.sql"
docker cp migrations/4.4.0/marten-9-38-quick-append-events.sql "$PG:/tmp/marten938.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/marten938.sql >/dev/null

step "applying migrations/4.5.0/refresh-token-hash-index.sql"
docker cp migrations/4.5.0/refresh-token-hash-index.sql "$PG:/tmp/refresh-hash.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/refresh-hash.sql >/dev/null

# Data, not schema (#883). FROM_VERSION seeded a role named HR under the fixed id, whose holders read
# Sensitive fields by that name, and the working tree decides the same thing by capability. The
# file grants to that role and to no other role named HR. The count is checked before the new build
# boots, because its seeder grants the same capability and would hide a file that does nothing. The
# three checks are in lib-upgrade-data.sh.
step "applying migrations/4.6.0/sensitivity-by-capability.sql"
require_seeded_hr
docker cp migrations/4.6.0/sensitivity-by-capability.sql "$PG:/tmp/sensitivity.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/sensitivity.sql >/dev/null
require_seeded_hr_granted
echo "HR holds view_sensitive"

step "the migration left the daemon's progression alone"
PROGRESSION_MIGRATED=$(psql_q "select coalesce(max(last_seq_id), 0) from mt_event_progression where name like '%WorkflowProjection%';")
[ "$PROGRESSION_MIGRATED" = "$PROGRESSION_BEFORE" ] \
    || fail "the migration moved the workflow projection from $PROGRESSION_BEFORE to $PROGRESSION_MIGRATED. A reset here means the new build replays every event on first boot, re-firing every workflow email, webhook and task."
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

step "applying migrations/4.5.0/email-sent-emails.sql"
docker cp migrations/4.5.0/email-sent-emails.sql "$PG:/tmp/sent-emails.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/sent-emails.sql >/dev/null

step "applying migrations/4.6.0/external-auth-identities.sql"
docker cp migrations/4.6.0/external-auth-identities.sql "$PG:/tmp/external-identities.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/external-identities.sql >/dev/null

step "Suite db-assert must now pass, every module included"
run_suite db-assert >"$WORK/assert-after-suite.log" 2>&1 || {
    cat "$WORK/assert-after-suite.log" >&2
    fail "the migrations brought core up to date and left a module behind. The Suite, which the published image runs, would refuse to start on this database. Whatever db-assert lists above needs a migration, and docs/upgrading-to-4.0.md needs to name it."
}
echo "Suite schema matches"

step "booting the working tree's Suite in Production against the migrated database"
HOST_EXEC=exec run_suite >"$WORK/boot.log" 2>&1 &
HOST_PID=$!
NEW_PORT=$(listen_port "$WORK/boot.log" "$HOST_PID") || { cat "$WORK/boot.log" >&2; fail "the working tree's Suite this run started is not listening"; }
NEW_URL="http://127.0.0.1:${NEW_PORT}"
for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$NEW_URL/health" || true)" = "200" ] && break
    kill -0 "$HOST_PID" 2>/dev/null || { cat "$WORK/boot.log" >&2; fail "the working tree's Suite exited during startup"; }
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$NEW_URL/health")" = "200" ] || {
    cat "$WORK/boot.log" >&2; fail "the working tree's Suite never became healthy"
}
kill -0 "$HOST_PID" 2>/dev/null || {
    cat "$WORK/boot.log" >&2
    fail "something answered /health but the host we started is gone, so every check below would describe another process"
}

step "the ${FROM_VERSION} admin can still sign in"
NEW_TOKEN=$(curl -s -X POST "$NEW_URL/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('accessToken') or d.get('token') or '')")
[ -n "$NEW_TOKEN" ] || fail "the ${FROM_VERSION} admin cannot sign in to the working tree's build"

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
step "stopping the working tree's Suite before the rollback"
kill "$HOST_PID" 2>/dev/null || true
wait "$HOST_PID" 2>/dev/null || true
HOST_PID=""

# Newest first, the reverse of the order the forward files ran in. FROM_VERSION asserts its own
# schema and refuses to boot over an index or column it does not declare on a table it does. A whole
# table it does not declare does not stop it: a 4.x start boots again below with
# mt_doc_public_forms still there, since only rollback-to-3.x.sql drops that one. The two 4.3.0
# files touch different objects, so their order between themselves does not matter; both have to
# run before the older rollbacks.
step "applying migrations/4.6.0/rollback-external-auth-identities.sql"
docker cp migrations/4.6.0/rollback-external-auth-identities.sql "$PG:/tmp/external-identities-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/external-identities-down.sql >/dev/null
step "applying migrations/4.6.0/rollback-sensitivity-by-capability.sql"
docker cp migrations/4.6.0/rollback-sensitivity-by-capability.sql "$PG:/tmp/sensitivity-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/sensitivity-down.sql >/dev/null
require_seeded_hr_not_granted
step "applying migrations/4.5.0/rollback-email-sent-emails.sql"
docker cp migrations/4.5.0/rollback-email-sent-emails.sql "$PG:/tmp/sent-emails-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/sent-emails-down.sql >/dev/null
step "applying migrations/4.5.0/rollback-refresh-token-hash-index.sql"
docker cp migrations/4.5.0/rollback-refresh-token-hash-index.sql "$PG:/tmp/refresh-hash-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/refresh-hash-down.sql >/dev/null

step "applying migrations/4.4.0/rollback-marten-9-38-quick-append-events.sql"
docker cp migrations/4.4.0/rollback-marten-9-38-quick-append-events.sql "$PG:/tmp/marten938-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/marten938-down.sql >/dev/null

step "applying migrations/4.3.0/rollback-collection-syncs.sql"
docker cp migrations/4.3.0/rollback-collection-syncs.sql "$PG:/tmp/collection-syncs-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/collection-syncs-down.sql >/dev/null

step "applying migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql"
docker cp migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql "$PG:/tmp/marten937-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/marten937-down.sql >/dev/null

step "applying migrations/4.2.0/rollback-site-share-links.sql"
docker cp migrations/4.2.0/rollback-site-share-links.sql "$PG:/tmp/share-links-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/share-links-down.sql >/dev/null

step "applying migrations/4.2.0/rollback-user-normalized-identity.sql"
docker cp migrations/4.2.0/rollback-user-normalized-identity.sql "$PG:/tmp/users-down.sql"
docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/users-down.sql >/dev/null

if [ "$FROM_3X" = 1 ]; then
    step "applying migrations/4.0.0/rollback-to-3.x.sql"
    docker cp migrations/4.0.0/rollback-to-3.x.sql "$PG:/tmp/down.sql"
    docker exec "$PG" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 --single-transaction -f /tmp/down.sql >/dev/null
fi

step "booting ${FROM_VERSION} again against the rolled-back database"
docker start "$OLD" >/dev/null
old_url
for _ in $(seq 1 60); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health" || true)" = "200" ] && break
    sleep 2
done
[ "$(curl -s -o /dev/null -w '%{http_code}' "$OLD_URL/health")" = "200" ] || {
    echo "--- ${FROM_VERSION} container after rollback ---" >&2
    docker logs "$OLD" 2>&1 | tail -30 >&2
    fail "${FROM_VERSION} did not come back up against the rolled-back database. The documented rollback did not land on a schema ${FROM_VERSION} recognises."
}
old_is_running
echo "${FROM_VERSION} is healthy again"

step "${FROM_VERSION} still serves the record the working tree's build wrote to, after the rollback"
ROLLBACK_TOKEN=$(curl -s -X POST "$OLD_URL/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('accessToken') or d.get('token') or '')")
[ -n "$ROLLBACK_TOKEN" ] || fail "the admin cannot sign in to ${FROM_VERSION} after rollback"

ROLLBACK_FIRST_NAME=$(curl -s "$OLD_URL/api/contents/$CONTENT_ID" -H "Authorization: Bearer $ROLLBACK_TOKEN" \
    | python3 -c "import sys,json; print(json.load(sys.stdin).get('data',{}).get('FirstName',''))")
[ "$ROLLBACK_FIRST_NAME" = "Upgrade" ] \
    || fail "expected content $CONTENT_ID to still read back FirstName 'Upgrade' through ${FROM_VERSION} after rollback, got '$ROLLBACK_FIRST_NAME'"

# The status change was made under the new build (newStatus 2, Archived). It lives on the document itself, not
# only in the event stream, and rollback does not touch mt_doc_contents rows, so it must still be 2.
# Read straight from the column rather than through the API: the JSON enum name is a detail of the new build
# this test has no need to depend on.
ROLLBACK_STATUS=$(psql_q "select data ->> 'Status' from mt_doc_contents where id = '$CONTENT_ID';")
[ "$ROLLBACK_STATUS" = "2" ] \
    || fail "expected content $CONTENT_ID to still have Status 2 (set by the working tree's build) after rollback, got '$ROLLBACK_STATUS'"

EVENTS_ROLLED_BACK=$(psql_q "select count(*) from mt_events where stream_id = '$CONTENT_ID';")
[ "$EVENTS_ROLLED_BACK" = "$EVENTS_AFTER" ] \
    || fail "expected $EVENTS_AFTER events on stream $CONTENT_ID after rollback, found $EVENTS_ROLLED_BACK. A rollback must not lose events."
echo "${FROM_VERSION} reads it back: FirstName $ROLLBACK_FIRST_NAME, Status $ROLLBACK_STATUS, $EVENTS_ROLLED_BACK events on the stream"

if [ "$FROM_3X" = 1 ]; then
    UP_FIRST="migrations/4.0.0/3.x-to-4.0.sql, "
    DOWN_LAST="migrations/4.2.0/rollback-site-share-links.sql, migrations/4.2.0/rollback-user-normalized-identity.sql and migrations/4.0.0/rollback-to-3.x.sql"
else
    UP_FIRST=""
    DOWN_LAST="migrations/4.2.0/rollback-site-share-links.sql and migrations/4.2.0/rollback-user-normalized-identity.sql"
fi

printf '\nThe upgrade from %s to the working tree works on the Suite host, with %smigrations/4.2.0/user-normalized-identity.sql, migrations/4.2.0/site-share-links.sql, migrations/4.3.0/collection-syncs.sql, migrations/4.3.0/marten-9-37-event-store-columns.sql, migrations/4.4.0/marten-9-38-quick-append-events.sql, migrations/4.5.0/refresh-token-hash-index.sql, migrations/4.6.0/sensitivity-by-capability.sql, migrations/4.2.0/stored-files-parent-index.sql, migrations/4.2.0/forms-public-forms.sql, migrations/4.5.0/email-sent-emails.sql and migrations/4.6.0/external-auth-identities.sql applied first, and rolls back cleanly with migrations/4.6.0/rollback-external-auth-identities.sql, migrations/4.6.0/rollback-sensitivity-by-capability.sql, migrations/4.5.0/rollback-email-sent-emails.sql, migrations/4.5.0/rollback-refresh-token-hash-index.sql, migrations/4.4.0/rollback-marten-9-38-quick-append-events.sql, migrations/4.3.0/rollback-collection-syncs.sql, migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql, %s.\n' "$FROM_VERSION" "$UP_FIRST" "$DOWN_LAST"

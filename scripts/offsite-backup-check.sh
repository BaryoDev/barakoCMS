#!/usr/bin/env bash
#
# Proves the off-host copy of the nightly backup (#780): the backup script uploads a checked dump
# to an S3-compatible server, the uploaded copy restores, and a failed upload fails the run loudly
# without deleting the local copy or rotating anything.
#
# The sequence:
#
#   1. a private network with SeaweedFS (the S3 server the Files.S3 tests use) and Postgres
#   2. the backup container from the compose files, running scripts/backup-cron.sh with
#      BACKUP_S3_* pointing at SeaweedFS, and a bucket made for it
#   3. the schema the backup waits for, with rows to count
#   4. the initial backup logs "Offsite copy OK"
#   5. the object is downloaded from the bucket and restored into a new database, and the rows
#      are counted against the original
#   6. a run with a wrong secret exits non-zero, logs BACKUP FAILED, keeps its archive and leaves
#      an archive older than the retention alone
#
# No host port is published: everything talks over the private network.
#
# The backup container installs curl from its Alpine repositories when the copy is configured. In a
# sandbox that cannot reach them, BACKUP_CHECK_CURL=/path/to/static/curl mounts that binary instead,
# and the script skips the install because curl is already there.
#
# Usage: scripts/offsite-backup-check.sh

set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$PWD"
RUN="offsite-check-$$"
NETWORK="$RUN-net"
S3="$RUN-s3"
DB="$RUN-db"
BACKUP="$RUN-backup"
ACCESS_KEY="offsite-check"
SECRET_KEY="offsite-check-not-a-real-secret"
BUCKET="barako-offsite"
PREFIX="nightly/"

cleanup() {
    docker rm -f "$BACKUP" "$DB" "$S3" >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() {
    printf '\nFAILED: %s\n' "$1" >&2
    docker logs "$BACKUP" 2>&1 | tail -30 >&2 || true
    exit 1
}

# The backup container's own curl, configured the way the script configures it.
s3() {
    docker exec "$BACKUP" sh -c "printf 'user = \"%s:%s\"\n' '$ACCESS_KEY' '$SECRET_KEY' > /tmp/check-credentials
        curl -sS --fail-with-body -K /tmp/check-credentials --aws-sigv4 'aws:amz:us-east-1:s3' $1"
}

step "network, SeaweedFS and Postgres"
docker network create "$NETWORK" >/dev/null
docker run -d --name "$S3" --network "$NETWORK" \
    -e AWS_ACCESS_KEY_ID="$ACCESS_KEY" -e AWS_SECRET_ACCESS_KEY="$SECRET_KEY" \
    chrislusf/seaweedfs:4.46@sha256:08d516132314207d10c8e37cbffc1f32b147d870169688734cc61c6231625b62 mini >/dev/null
docker run -d --name "$DB" --network "$NETWORK" \
    -e POSTGRES_PASSWORD=offsite-check -e POSTGRES_DB=barakocms postgres:16-alpine >/dev/null
for _ in $(seq 1 60); do
    docker exec "$DB" pg_isready -U postgres -d barakocms >/dev/null 2>&1 && break
    sleep 1
done

step "the backup container, with the offsite copy configured"
extra=()
if [ -n "${BACKUP_CHECK_CURL:-}" ]; then
    extra=(-v "$BACKUP_CHECK_CURL:/usr/local/bin/curl:ro")
fi
docker run -d --name "$BACKUP" --network "$NETWORK" \
    -v "$REPO/scripts:/scripts:ro" "${extra[@]}" \
    -e POSTGRES_HOST="$DB" -e POSTGRES_DB=barakocms -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=offsite-check \
    -e BACKUP_CRON_SCHEDULE="0 2 * * *" -e BACKUP_KEEP_DAYS=7 \
    -e BACKUP_S3_BUCKET="$BUCKET" -e BACKUP_S3_PREFIX="$PREFIX" -e BACKUP_S3_REGION=us-east-1 \
    -e BACKUP_S3_ENDPOINT="http://$S3:8333" \
    -e BACKUP_S3_ACCESS_KEY="$ACCESS_KEY" -e BACKUP_S3_SECRET_KEY="$SECRET_KEY" \
    --entrypoint /bin/sh postgres:16-alpine /scripts/backup-cron.sh >/dev/null

ready=0
for _ in $(seq 1 60); do
    if docker exec "$BACKUP" sh -c 'command -v curl' >/dev/null 2>&1 \
        && s3 "-X PUT http://$S3:8333/$BUCKET" >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 2
done
[ "$ready" = 1 ] || fail "could not make the bucket through the backup container's curl"
docker logs "$BACKUP" 2>&1 | grep -q "offsite:   s3://$BUCKET/$PREFIX" || fail "the daemon did not report the offsite copy at start"

step "the schema the backup waits for"
docker exec "$DB" psql -U postgres -d barakocms -v ON_ERROR_STOP=1 -q -c "
    create table public.mt_doc_users (id uuid primary key, data jsonb not null);
    insert into public.mt_doc_users
        select gen_random_uuid(), jsonb_build_object('n', g, 'pad', repeat('x', 64))
        from generate_series(1, 250) g;" || fail "could not create the schema"

step "waiting for the initial backup and its offsite copy"
ok=0
for _ in $(seq 1 90); do
    logs=$(docker logs "$BACKUP" 2>&1 || true)
    if printf '%s' "$logs" | grep -q "Offsite copy OK"; then ok=1; break; fi
    if printf '%s' "$logs" | grep -q "BACKUP FAILED"; then break; fi
    sleep 2
done
printf '%s\n' "$logs" | tail -12
[ "$ok" = 1 ] || fail "the initial backup did not log an offsite copy"

key=$(printf '%s' "$logs" | sed -n "s#.*Offsite copy OK .*: s3://$BUCKET/\(.*\)#\1#p" | tail -1)
[ -n "$key" ] || fail "could not read the uploaded key from the log"
echo "uploaded: s3://$BUCKET/$key"

step "restoring from the uploaded copy"
s3 "-o /tmp/offsite.sql.gz http://$S3:8333/$BUCKET/$key" || fail "could not download the uploaded copy"
docker exec "$BACKUP" sh -c "gzip -t /tmp/offsite.sql.gz" || fail "the downloaded copy is not a valid gzip"
docker exec "$DB" psql -U postgres -v ON_ERROR_STOP=1 -q -c "create database restored" || fail "could not create the restore target"
docker exec "$BACKUP" sh -c "gunzip -c /tmp/offsite.sql.gz | PGPASSWORD=offsite-check psql -h $DB -U postgres -d restored -v ON_ERROR_STOP=1 -q" \
    >/dev/null || fail "the uploaded copy did not restore"
original=$(docker exec "$DB" psql -U postgres -d barakocms -tAc "select count(*) from public.mt_doc_users")
restored=$(docker exec "$DB" psql -U postgres -d restored -tAc "select count(*) from public.mt_doc_users")
echo "rows: original $original, restored $restored"
[ "$original" = 250 ] && [ "$restored" = "$original" ] || fail "the restored database has $restored rows, expected $original"

step "a failed upload fails the run, keeps the archive and rotates nothing"
docker exec "$BACKUP" sh -c 'touch -d "@$(( $(date +%s) - 30 * 86400 ))" /backups/barako_backup_2000-01-01_00-00-00.sql.gz'
before=$(docker exec "$BACKUP" sh -c 'ls -1 /backups/barako_backup_*.sql.gz | wc -l')
set +e
failed_output=$(docker exec -e BACKUP_S3_SECRET_KEY=wrong-secret "$BACKUP" /backup_job.sh 2>&1)
failed_exit=$?
set -e
printf '%s\n' "$failed_output" | tail -6
[ "$failed_exit" -ne 0 ] || fail "a run whose upload was refused exited 0"
printf '%s' "$failed_output" | grep -q "BACKUP FAILED.*offsite copy" || fail "the failed upload was not reported as BACKUP FAILED"
after=$(docker exec "$BACKUP" sh -c 'ls -1 /backups/barako_backup_*.sql.gz | wc -l')
[ "$after" -eq $((before + 1)) ] || fail "the local archive of the failed run was not kept ($before before, $after after)"
docker exec "$BACKUP" test -f /backups/barako_backup_2000-01-01_00-00-00.sql.gz \
    || fail "rotation ran after a failed upload and deleted an old archive"
printf '%s' "$failed_output" | grep -q "$SECRET_KEY\|wrong-secret" && fail "the secret appeared in the output"

printf '\nThe backup was copied off the host, restored from that copy, and a refused upload failed loudly.\n'

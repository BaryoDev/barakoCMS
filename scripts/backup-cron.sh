#!/bin/sh
# Nightly PostgreSQL backup daemon.
#
# The previous version piped pg_dump straight into gzip and then checked $?,
# which is gzip's exit status, not pg_dump's. gzip succeeds on empty input, so a
# failing dump still reported "backup success" and left a 20-byte file behind.
# That is not hypothetical: the database credentials were wrong for months and
# every nightly backup was an empty gzip that claimed to be fine.
#
# So dump to a temp file, check pg_dump itself, prove the archive decompresses
# and looks like a dump, and only then publish it. A backup that fails has to
# fail loudly and leave nothing behind that looks like a backup.

set -eu

BACKUP_DIR=/backups
mkdir -p "$BACKUP_DIR"

echo "Starting backup daemon"
echo "  schedule:  $BACKUP_CRON_SCHEDULE"
echo "  retention: $BACKUP_KEEP_DAYS days"

cat <<'JOB' > /backup_job.sh
#!/bin/sh
set -eu

BACKUP_DIR=/backups
# A dump of an empty schema is still comfortably over 1KB, so anything smaller
# means something went wrong upstream.
MIN_BYTES="${BACKUP_MIN_BYTES:-1000}"
TIMESTAMP=$(date +%Y-%m-%d_%H-%M-%S)
FINAL="$BACKUP_DIR/barako_backup_$TIMESTAMP.sql.gz"
TMP="$BACKUP_DIR/.in-progress_$TIMESTAMP.sql"

fail() {
    echo "BACKUP FAILED [$(date)]: $1" >&2
    rm -f "$TMP" "$TMP.gz"
    exit 1
}

echo "Starting backup [$(date)] -> $FINAL"

# 1. Dump to a plain file, so pg_dump's own exit code is the one we check.
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump \
    -h "$POSTGRES_HOST" -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
    > "$TMP" || fail "pg_dump exited non-zero"

# 2. An auth failure writes nothing at all, so insist on real content.
[ -s "$TMP" ] || fail "pg_dump produced an empty file"
grep -q "PostgreSQL database dump" "$TMP" || fail "output does not look like a pg_dump"

# 3. Compress, then prove the archive is readable before trusting it.
gzip "$TMP" || fail "gzip failed"
gzip -t "$TMP.gz" || fail "the compressed archive is corrupt"

SIZE=$(wc -c < "$TMP.gz")
[ "$SIZE" -ge "$MIN_BYTES" ] || fail "archive is only ${SIZE} bytes, expected at least ${MIN_BYTES}"

# 4. Only now does it get to be called a backup.
mv "$TMP.gz" "$FINAL"
echo "Backup OK [$(date)]: $FINAL (${SIZE} bytes)"

# 5. Rotate only after a success, so a run of failures can never delete the last
#    good backup we have.
find "$BACKUP_DIR" -name "barako_backup_*.sql.gz" -mtime "+$BACKUP_KEEP_DAYS" -exec rm {} \;
find "$BACKUP_DIR" -name ".in-progress_*" -mtime +1 -exec rm {} \;
echo "Rotation done, keeping $BACKUP_KEEP_DAYS days"
JOB

chmod +x /backup_job.sh

# Run once at startup so a broken backup surfaces at deploy time rather than in
# six months, which is how the last one stayed broken.
# Wait for there to be something worth backing up.
#
# Postgres accepting connections is not the same as the schema existing. On a fresh stack this
# container starts as soon as the database is healthy, which races the API creating its tables, and
# the proof backup below then failed on every first deployment with "archive is only 368 bytes".
# Nothing was lost, but the stack had no recovery point until an operator noticed, and a failure
# logged on every first deploy teaches people to ignore this log.
#
# Asked of Postgres rather than of the API's readiness endpoint on purpose: this is the actual
# precondition, it needs no second service to be reachable, and it works against every published
# image. /health/ready would have been the tidier signal and it does not exist before 4.0.
WAIT_SECONDS="${BACKUP_SCHEMA_WAIT_SECONDS:-300}"
waited=0
schema_ready=0
while [ "$waited" -lt "$WAIT_SECONDS" ]; do
    if PGPASSWORD="$POSTGRES_PASSWORD" psql -h "$POSTGRES_HOST" -U "$POSTGRES_USER" \
        -d "$POSTGRES_DB" -tAc "select to_regclass('public.mt_doc_users') is not null" 2>/dev/null \
        | grep -q '^t$'; then
        schema_ready=1
        break
    fi
    # The smaller of five seconds and what is left, so a configured limit is honoured rather than
    # rounded up to the next step. BACKUP_SCHEMA_WAIT_SECONDS=1 should wait one second, not five.
    remaining=$((WAIT_SECONDS - waited))
    step=5
    [ "$remaining" -lt 5 ] && step="$remaining"
    sleep "$step"
    waited=$((waited + step))
done

if [ "$schema_ready" = 1 ]; then
    echo "Application schema present after ${waited}s"
    echo "Running an initial backup to prove the configuration works"
    /backup_job.sh || echo "WARNING: the initial backup failed, see the error above"
else
    # Not fatal, and deliberately not attempted. Dumping now produces the tiny archive this wait
    # exists to avoid, so it would fail loudly for a reason already reported and teach an operator
    # to ignore this log. The schedule stays active and tonight's backup runs normally.
    echo "WARNING: no application schema after ${WAIT_SECONDS}s, so there is nothing to back up yet." >&2
    echo "         The initial backup is skipped. The nightly schedule is still active." >&2
    echo "         Check that the API started." >&2
fi

echo "$BACKUP_CRON_SCHEDULE /backup_job.sh >> /var/log/cron.log 2>&1" > /etc/crontabs/root

crond -f -l 2

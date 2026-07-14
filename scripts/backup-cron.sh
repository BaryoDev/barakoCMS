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
echo "Running an initial backup to prove the configuration works"
/backup_job.sh || echo "WARNING: the initial backup failed, see the error above"

echo "$BACKUP_CRON_SCHEDULE /backup_job.sh >> /var/log/cron.log 2>&1" > /etc/crontabs/root

crond -f -l 2

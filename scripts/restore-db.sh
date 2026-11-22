#!/bin/bash

# Usage: ./restore-db.sh <backup-file>
#
# On a stack with the postgres-extensions job (docker-compose.prod.yml) the restore runs as the app
# role: the owner job creates the role and the extensions first, and the dump is loaded with
# PGOPTIONS='-c role=<app role>', so every table, function and index it creates belongs to the app
# role. CREATE EXTENSION and COMMENT ON EXTENSION lines are dropped from the stream, which the owner
# job has already covered. On a stack without that job (docker-compose.yml) the restore runs as the
# owner, and the script says so.

if [ -z "$1" ]; then
  echo "❌ Error: Please specify a backup file to restore."
  echo "Usage: ./restore-db.sh ./backups/barako_backup_YYYY-MM-DD_HH-MM-SS.sql.gz"
  echo "Available backups:"
  ls -lh ./backups/*.sql.gz 2>/dev/null || echo "No backups found."
  exit 1
fi

BACKUP_FILE=$1

if [ ! -f "$BACKUP_FILE" ]; then
  echo "❌ Error: File not found: $BACKUP_FILE"
  exit 1
fi

# Check the archive before anything is stopped or dropped: it must decompress, be non-empty and
# start like the plain pg_dump output scripts/backup-cron.sh writes.
if ! gunzip -t "$BACKUP_FILE" 2>/dev/null; then
  echo "Error: $BACKUP_FILE is not a valid gzip archive. Nothing was changed."
  exit 1
fi
HEADER="$(gunzip -c "$BACKUP_FILE" 2>/dev/null | head -c 4096 || true)"
if [ -z "$HEADER" ] || ! grep -q "PostgreSQL database dump" <<< "$HEADER"; then
  echo "Error: $BACKUP_FILE is empty or does not look like a pg_dump archive. Nothing was changed."
  exit 1
fi

echo "WARNING: This will OVERWRITE the current database 'barako_cms'."
read -p "Are you sure? (y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Restore cancelled."
    exit 1
fi

# Drops CREATE EXTENSION and COMMENT ON EXTENSION lines, and leaves COPY data blocks alone.
EXT_FILTER='c { print; if ($0 == "\\.") c = 0; next } /^COPY .* FROM stdin;$/ { c = 1 } /^(CREATE|COMMENT ON) EXTENSION / { next } { print }'

OWNER="${DB_USER:-postgres}"
APP_ROLE="${DB_APP_USER:-}"
if [ -z "$APP_ROLE" ] && [ -f .env ]; then
  APP_ROLE="$(sed -n 's/^DB_APP_USER=//p' .env | tail -n 1 | tr -d '"'"'"'\r')"
fi
APP_ROLE="${APP_ROLE:-barako_app}"
if ! [[ "$APP_ROLE" =~ ^[a-z_][a-z0-9_]*$ ]]; then
  echo "DB_APP_USER must be a plain lower case role name. Nothing was changed."
  exit 1
fi

if docker compose config --services 2>/dev/null | grep -qx postgres-extensions; then
  AS_APP_ROLE=1
else
  AS_APP_ROLE=0
  echo "This stack has no postgres-extensions job, so the restore runs as the database owner."
fi

echo "WARNING: This will OVERWRITE the current database 'barako_cms'."
read -p "Are you sure? (y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Restore cancelled."
    exit 1
fi

echo "Stopping application..."
docker compose stop app

# Drop and recreate the database, owned by the same user. The dump creates objects, so it needs an
# empty target, and ON_ERROR_STOP would stop at the first CREATE on a populated one.
echo "Dropping the database barako_cms..."
if ! docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$OWNER" -d postgres \
    -c 'drop database if exists barako_cms with (force)'; then
  echo "Could not drop the database, so it is as it was. Restarting the application."
  docker compose start app
  exit 1
fi

# From here the old data is gone. A failure leaves the application stopped, so it cannot start on an
# empty or partial database and create fresh tables and a new admin over the loss.
RETRY="Retry with the same file: ./restore-db.sh $BACKUP_FILE"
if ! docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$OWNER" -d postgres \
    -c "create database barako_cms owner \"$OWNER\""; then
  echo "The database barako_cms was dropped and not created. It does not exist now."
  echo "The application is left stopped. $RETRY"
  exit 1
fi

if [ "$AS_APP_ROLE" = 1 ]; then
  # Only the owner job: it creates the app role and the extensions in the empty database. The API
  # starts after the restore, so nothing else is brought up here.
  echo "Running the owner job..."
  if ! docker compose run --rm --no-deps postgres-extensions; then
    echo "The owner job failed. The application is left stopped. $RETRY"
    exit 1
  fi
fi

echo "Restoring from $BACKUP_FILE..."
set -o pipefail
restore() {
  if [ "$AS_APP_ROLE" = 1 ]; then
    gunzip -c "$BACKUP_FILE" | awk "$EXT_FILTER" \
      | docker compose exec -T -e PGOPTIONS="-c role=$APP_ROLE" postgres psql -v ON_ERROR_STOP=1 -U "$OWNER" -d barako_cms
  else
    gunzip -c "$BACKUP_FILE" | docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$OWNER" -d barako_cms
  fi
}
if restore; then
    echo "Restore complete."
else
    echo "Restore failed. The database barako_cms was dropped and is now empty or partly restored."
    echo "The application is left stopped. $RETRY"
    exit 1
fi

echo "Restarting application..."
if [ "$AS_APP_ROLE" = 1 ]; then
  docker compose up -d
else
  docker compose start app
fi
echo "Done."

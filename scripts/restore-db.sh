#!/bin/bash

# Usage: ./restore-db.sh <backup-file>

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

echo "Stopping application..."
docker compose stop app

# Drop and recreate the database, owned by the same user. The dump creates objects, so it needs an
# empty target, and ON_ERROR_STOP would stop at the first CREATE on a populated one.
echo "Dropping the database barako_cms..."
if ! docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U postgres -d postgres \
    -c 'drop database if exists barako_cms with (force)'; then
  echo "Could not drop the database, so it is as it was. Restarting the application."
  docker compose start app
  exit 1
fi

# From here the old data is gone. A failure leaves the application stopped, so it cannot start on an
# empty or partial database and create fresh tables and a new admin over the loss.
RETRY="Retry with the same file: ./restore-db.sh $BACKUP_FILE"
if ! docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U postgres -d postgres \
    -c 'create database barako_cms owner postgres'; then
  echo "The database barako_cms was dropped and not created. It does not exist now."
  echo "The application is left stopped. $RETRY"
  exit 1
fi

echo "Restoring from $BACKUP_FILE..."
set -o pipefail
if gunzip -c "$BACKUP_FILE" | docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U postgres -d barako_cms; then
    echo "Restore complete."
else
    echo "Restore failed. The database barako_cms was dropped and is now empty or partly restored."
    echo "The application is left stopped. $RETRY"
    exit 1
fi

echo "Restarting application..."
docker compose start app
echo "Done."

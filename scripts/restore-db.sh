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

echo "⚠️  WARNING: This will OVERWRITE the current database 'barako_cms'."
read -p "Are you sure? (y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "🚫 Restore cancelled."
    exit 1
fi

echo "🛑 Stopping application..."
docker compose stop app

# Drop and recreate the database, owned by the same user. The dump creates objects, so it needs an
# empty target, and ON_ERROR_STOP would stop at the first CREATE on a populated one.
echo "Recreating the database barako_cms..."
if ! docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U postgres -d postgres \
    -c 'drop database if exists barako_cms with (force)' \
    -c 'create database barako_cms owner postgres'; then
  echo "Could not recreate the database. Nothing was restored."
  docker compose start app
  exit 1
fi

echo "📦 Restoring from $BACKUP_FILE..."
gunzip -c "$BACKUP_FILE" | docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U postgres -d barako_cms

if [ $? -eq 0 ]; then
    echo "✅ Restore complete!"
else
    echo "❌ Restore failed."
    docker compose start app
    exit 1
fi

echo "🚀 Restarting application..."
docker compose start app
echo "✅ Done."

#!/bin/sh

# Ensure backup directory exists
mkdir -p /backups

echo "🚀 Starting Backup Daemon..."
echo "📅 Schedule: $BACKUP_CRON_SCHEDULE"
echo "♻️  Retention: $BACKUP_KEEP_DAYS days"

# Create a backup script
cat <<EOF > /backup_job.sh
#!/bin/sh
TIMESTAMP=\$(date +%Y-%m-%d_%H-%M-%S)
FILENAME="/backups/barako_backup_\$TIMESTAMP.sql.gz"

echo "📦 [\$(date)] Starting backup: \$FILENAME"

PGPASSWORD=\$POSTGRES_PASSWORD pg_dump -h \$POSTGRES_HOST -U \$POSTGRES_USER -d \$POSTGRES_DB | gzip > \$FILENAME

if [ \$? -eq 0 ]; then
  echo "✅ [\$(date)] Backup success: \$FILENAME"
  
  # Retention Policy: Delete files older than X days
  find /backups -name "barako_backup_*.sql.gz" -mtime +$BACKUP_KEEP_DAYS -exec rm {} \;
  echo "🧹 [\$(date)] Cleaned up old backups"
else
  echo "❌ [\$(date)] Backup FAILED"
fi
EOF

chmod +x /backup_job.sh

# Add to crontab
echo "$BACKUP_CRON_SCHEDULE /backup_job.sh >> /var/log/cron.log 2>&1" > /etc/crontabs/root

# Start cron in foreground
crond -f -l 2

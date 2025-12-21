# Database Automation

BarakoCMS includes built-in tools for automated database management, ensuring your data is safe and easy to restore without deep DBA knowledge.

---

## Automated Backups

For production environments using Docker Compose, we include a **Backup Sidecar** service.

*   **Schedule**: Runs automatically at **2:00 AM** daily.
*   **Location**: Backups are stored locally in the `./backups` directory.
*   **Retention**: Keeps the last **7 days** of backups (older files are auto-deleted).

### Configuration

You can adjust the schedule and retention in `docker-compose.yml`:

```yaml
environment:
  # Cron expression (e.g., "0 2 * * *" = 2:00 AM)
  BACKUP_CRON_SCHEDULE: "0 2 * * *"
  
  # Number of days to keep files
  BACKUP_KEEP_DAYS: 7
```

---

## Manual Operations

### Trigger a Backup Immediately

If you need to make a snapshot before a deployment:

```bash
docker compose exec db-backup /backup_job.sh
```

You will see a new file in your `./backups` folder: `barako_backup_YYYY-MM-DD_...sql.gz`.

### Restoring Data

We provide a helper script to restore your database from a backup file.

**⚠️ Warning**: This will overwrite your current database!

```bash
# 1. Make the script executable (only needed once)
chmod +x scripts/restore-db.sh

# 2. Run the restore command
./scripts/restore-db.sh ./backups/barako_backup_2025-12-16_02-00-00.sql.gz
```

The script will:
1.  Ask for confirmation.
2.  Stop the application container (to verify no locks).
3.  Drop and recreate the database schema from the backup.
4.  Restart the application.

---

## Schema Migrations

BarakoCMS uses **Marten** for document storage. Database schema changes are handled automatically on application startup.

### Development Mode
*   **Behavior**: `AutoCreate.All`
*   **What it means**: The app can make destructuve changes to the schema. Great for rapid prototyping.

### Production Mode
*   **Behavior**: `AutoCreate.CreateOrUpdate`
*   **What it means**: The app will only make **safe, additive changes** (e.g., adding a column/index). It will never drop tables or columns, preventing accidental data loss during updates.

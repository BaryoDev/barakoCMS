# Backup & Recovery

BarakoCMS provides a comprehensive backup system for disaster recovery and data protection.

## Overview

The backup system creates JSON snapshots of the event store, allowing you to:
- **Create** point-in-time backups
- **Download** backups for offsite storage
- **Restore** system state from a backup
- **Delete** old backups to free space

## API Endpoints

| Method   | Endpoint                     | Description            |
| -------- | ---------------------------- | ---------------------- |
| `POST`   | `/api/backups`               | Create a new backup    |
| `GET`    | `/api/backups`               | List all backups       |
| `GET`    | `/api/backups/{id}/download` | Download a backup file |
| `POST`   | `/api/backups/{id}/restore`  | Restore from backup    |
| `DELETE` | `/api/backups/{id}`          | Delete a backup        |

::: warning Security
All backup endpoints require **SuperAdmin** role. Unauthorized requests return `401`.
:::

## Using the Admin UI

The Admin UI provides a visual interface at `/ops/backups`:

1. **Create Backup** - Click the button to create a snapshot
2. **Download** - Click the download icon to save locally
3. **Restore** - Click restore and confirm in the dialog
4. **Delete** - Click trash icon and confirm deletion

## Configuration

### Backup Directory

By default, backups are stored in `{ContentRootPath}/Backups`. Override with:

```bash
export BARAKO_BACKUP_DIR=/path/to/persistent/storage
```

### Size Limits

The `IBackupService` enforces a 10GB default limit:

```csharp
services.AddScoped<IBackupService, BackupService>();
```

## Security Features

### RBAC Protection

All endpoints require `Roles("SuperAdmin")`:

```csharp
public override void Configure()
{
    Post("/api/backups");
    Roles("SuperAdmin");
}
```

### Path Traversal Protection

Backup IDs are validated to prevent directory traversal attacks:

```csharp
if (id.Contains("..") || id.Contains("/") || id.Contains("\\"))
{
    await SendAsync(new { message = "Invalid backup ID" }, 400, ct);
    return;
}
```

## Best Practices

1. **Schedule Regular Backups** - Use cron or Task Scheduler
2. **Offsite Storage** - Download and store backups externally
3. **Test Restores** - Periodically verify backup integrity
4. **Monitor Disk Space** - Backups consume storage over time

## Example: Cron Job

```bash
#!/bin/bash
# backup-cron.sh
curl -X POST http://localhost:5006/api/backups \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json"
```

Schedule with:
```bash
# Daily at 2 AM
0 2 * * * /path/to/backup-cron.sh
```

## Future Enhancements

- Full restore functionality (currently simulated)
- Backup encryption at rest
- Cloud storage integration (S3, Azure Blob)
- Integrity verification checksums

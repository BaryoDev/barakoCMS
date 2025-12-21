# Migration Guide

## Upgrading from v2.0 to v2.1

### Overview
Version 2.1 introduces critical bug fixes, UI improvements, and security hardening for Backups.

### Breaking Changes
- **Backup Endpoints:** Now require `SuperAdmin` role.
- **Backup Path Validation:** Strict path traversal checks are enforced.
- **Admin UI:** Requires `NEXT_PUBLIC_API_URL` to be correctly set.

### Database Migrations
No schema changes are required for existing content. Marten will automatically update internal metadata on startup.

### Upgrade Steps

1. **Stop the Application**
   ```bash
   systemctl stop barako-web
   ```

2. **Deploy Backend Binaries**
   Copy the new contents of `bin/Release/net8.0/publish` to your server.

3. **Update Frontend Environment**
   Ensure `.env.local` contains:
   ```bash
   NEXT_PUBLIC_API_URL=http://localhost:5006
   ```

4. **Restart Application**
   ```bash
   systemctl start barako-web
   ```

5. **Verify Installation**
   - Log in to the Admin Dashboard.
   - Go to **Operations > Health** to verify system status.
   - Go to **Operations > Backups** and verify you can list backups.

### Rollback Procedure
If you encounter critical issues:
1. Stop the application.
2. Restore the previous binary files from your backup.
3. Restart the application.

# Troubleshooting Guide

This guide provides solutions to common issues you might encounter while running or deploying BarakoCMS.

## Admin UI Issues

### Login Loops or "Invalid Credentials" after Success
**Symptoms:** You enter correct credentials, but get an error or stay on the login page.
**Solution:**
1. Clear your browser cookies and local storage.
2. Ensure the backend API is running at `http://localhost:5006`.
3. Check browser console for Network errors (CORS issues).
4. Verify `NEXT_PUBLIC_API_URL` in `.env.local` matches your backend URL.

### Schema Edit "404 Not Found"
**Symptoms:** Clicking "Edit" on a content type shows a 404 page.
**Solution:**
This was a known issue in v2.0. Ensure you are running v2.1+ which includes the dynamic route `admin/src/app/schemas/[name]/page.tsx`.
If persisting:
- Rebuild the admin app: `rm -rf .next && npm run build`
- Restart the Next.js server.

### Backup List Not Updating
**Symptoms:** After creating a backup, it doesn't appear in the list immediately.
**Solution:**
- Click the "Refresh" button manually if available.
- Ensure your backend has write permissions to the `Backups` directory.
- Check backend logs for file permission errors.

## Backend Issues

### Database Connection Failures
**Symptoms:** "Connection refused" or Marten configuration errors.
**Solution:**
- Ensure PostgreSQL is running (Docker container or local service).
- Verify connection string in `appsettings.json`.
- Check if port 5432 is exposed and accessible.

### "Path Traversal" Security Errors
**Symptoms:** 400 Bad Request or 403 Forbidden when restoring/deleting backups.
**Solution:**
- BarakoCMS strictly validates filenames. Ensure you are not using `..` or slashes in backup filenames.
- Only restore files that appear in the backup list API.

## API & Integration

### CORS Errors
**Symptoms:** Frontend cannot fetch data; Browser console shows CORS policy block.
**Solution:**
- Configure `AllowedOrigins` in `appsettings.json` on the backend.
- Ensure the Admin UI URL is whitelisted.
- On Fly.io, verify the `CORS__AllowedOrigins` secret matches the Admin UI hostname.

## Cloud Deployment (Fly.io)

### "instance refused connection" or 502 Bad Gateway
**Symptoms:** The app is running but refuses connections; Fly logs show "load balancing" errors.
**Solution:**
- Ensure the application is listening on `0.0.0.0:8080` (not `127.0.0.1`).
- Check if the machine is hitting Out-of-Memory (OOM) limits. Scale memory: `fly scale memory 1024`.

### "Thread Pool Starvation" Warnings
**Symptoms:** Logs show heartbeats taking longer than 1 second.
**Solution:**
- This usually indicates CPU or Memory pressure.
- Disable redundant startup tasks. For example, ensure `DataSeeder` only runs when necessary.
- Disable optional heavy services like Kubernetes monitoring if not in a K8s environment.

### Deployment Hangs / Health Check Failures
**Symptoms:** `fly deploy` times out waiting for health checks.
**Solution:**
- Verify if you have `app.UseHealthChecksUI()` enabled without the corresponding service `AddHealthChecksUI()`.
- Ensure the `/health` endpoint is configured and returning 200 OK within the timeout period.
- Increase the timeout in `fly.toml` under the `[[services]]` section if needed.

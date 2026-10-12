- **Where a tenant's files were stored could only be set by an operator, with a restart.**
  `GET` and `PUT /api/settings/storage` let a tenant choose Postgres or an S3-compatible bucket,
  and `POST /api/settings/storage/test` puts, reads and deletes a test object first and names the
  step that failed. The secret key is write-only and stored encrypted. New uploads follow the saved
  setting from the next request, and every file is read from where it was stored, so nothing moves;
  pointing the saved bucket elsewhere while files are in it is refused.
  Any `Files:S3` key in configuration still wins and makes the setting read-only. A bucket address
  typed into the API must be public unless `Modules:Files.S3:AllowPrivateServiceUrl` is set. New
  capability `manage_storage`, held by SuperAdmin and Admin. **Upgrade step:** run
  `migrations/5.0.0/files-storage-settings.sql` (or `db-migrate`) for the new table.

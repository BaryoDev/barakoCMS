- **`DELETE /api/files/{id}` now requires being the uploader or an admin, matching the download route.**
  A holder of `upload_files` could delete any file in the tenant, including one uploaded by another
  account, while the download route already refused that same account with a 404. Delete could
  destroy a file it could not read. The two gates now agree: `upload_files` still opens list,
  describe and edit for every file in the tenant, but delete and download both also need the
  uploader, or an account holding Admin or SuperAdmin. `docs/access-control.md` covers the split.

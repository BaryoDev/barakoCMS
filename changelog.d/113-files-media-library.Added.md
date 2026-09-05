- **Alt text, a caption and a where-used list on files, for a media library.** `PATCH /api/files/{id}`
  stores `alt` and `caption` with a file, `GET /api/files/{id}/meta` and the new `GET /api/files` list
  return them, and `GET /api/public/files/{id}/meta` hands them to a frontend for a public file only,
  404 otherwise like the bytes next door. The list takes `?q=` for a name substring and
  `?contentType=image/` for a type prefix, is paginated, and leaves out cached resizes.

  `GET /api/files/{id}/usage` lists the entries whose data references the file, matched by the id and
  by the storage key so a bare id, a download URL with or without `?w=`, and an object store's public
  URL are all found. `DELETE /api/files/{id}` is new and refuses with a 409 naming the first ten
  usages while any entry references the file; `?force=true` deletes anyway, along with the cached
  resizes and every blob behind them. A usage row's title goes through the same read permission
  and sensitivity checks as `GET /api/contents`, so a file used by a Sensitive entry still blocks a
  delete without telling the editor what the entry says. All of it is gated on the module's
  `upload_files` capability. The console half (grid, picker) is barakoBrew's.

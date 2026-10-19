- **Import a whole spreadsheet on the server.** `POST /api/import/spreadsheet` takes the file with
  the header row, content type and a column mapping, and creates an entry from every data row, not
  only the 500 that analyze previews. Same create permission and per-row error report as
  `POST /api/import/content`, one transaction, and a `dryRun` flag. Past `Import:MaxRecords`,
  `Import:MaxUploadBytes`, the expanded size limit, `Import:MaxColumns` or `Import:MaxSeconds` it refuses or stops and
  writes nothing. The new route is additive, so no contract number moves. See `docs/spreadsheet-import.md`.
  Analyze now also refuses a sheet with a cell past `Import:MaxColumns` or past Excel's
  1,048,576 rows, because the parser pads to the highest address it sees.

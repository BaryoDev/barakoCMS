- **`POST /api/import/analyze` refuses a spreadsheet it will not parse, before decompressing it.**
  The parser reads a whole sheet into memory before the 500-row preview cap can apply, so the cost of
  a request followed the expanded size rather than the uploaded size. An xlsx is a zip, and repetitive
  sheet XML compresses roughly fifteen to one, so the 10 MB request body limit did not bound the work.

  Measured: a 3.2 MB upload, well inside the body limit, expanded to 46 MB of sheet XML and took 98
  seconds and 968 MB to answer, returning a preview of 500 rows. The same file is now refused in 0.15
  seconds and 20 MB. The global rate limit of 100 requests a minute per address does not bound
  something that costs what the first figure costs.

  The limit is on the expanded size the archive declares, read from the zip's central directory
  without decompressing anything. Default 8 MB, configurable as `Import:MaxExpandedBytes`, and a
  refusal names the setting so an operator with a genuinely large file knows what to change. A CSV is
  not an archive and is unaffected: its expanded size is its uploaded size, which the body limit
  already bounds.

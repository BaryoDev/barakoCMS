# Spreadsheet import

`POST /api/import/analyze` previews at most 500 rows. `POST /api/import/spreadsheet` imports the
whole file.

## Request

Multipart form. Needs the create permission on the target content type, the same as
`POST /api/import/content`, checked for the type and again for each row as it will be stored.

| Field | Meaning |
| --- | --- |
| `file` | The .xlsx or CSV. |
| `contentType` | Target content type. Must exist. |
| `headerRow` | Zero-based header row, numbered as analyze numbers rows. Data starts on the next row. Default 0. |
| `mapping` | JSON object of field name to zero-based column index, for example `{"Title":0,"Body":2}`. |
| `continueOnError` | `false` (default): one bad row writes nothing. `true`: good rows are written, bad ones reported. |
| `status` | `Draft`, `Published` or `Archived`, by name. Default `Published`. Anything else, a number included, is a 400. |
| `dryRun` | `true` checks every row and writes nothing. |

Mapping keys that differ only by case are refused. Blank cells are left out of the entry, so a required field fails on that row. A row with every
mapped cell blank is skipped and counted in `skippedBlankRows`.

## Response

`created`, `failed`, `dataRows`, `skippedBlankRows`, `dryRun` and `errors`. Each error's `row` is
the zero-based sheet row, so it matches the analyze grid. With `continueOnError` off and any bad
row the answer is 400 with the same body and nothing is written.

## Limits

Analyze applies the column check and caps row addresses at Excel's own 1,048,576, so a sheet with a cell far past those is refused there too; it accepts every sheet it accepted before. Nothing is truncated. Past a limit the request is refused and nothing is written.

| Limit | Default | Setting | Past it |
| --- | --- | --- | --- |
| Upload size | 10 MB | `Import:MaxUploadBytes` | 400 |
| Expanded size of an .xlsx | 8 MB | `Import:MaxExpandedBytes` | 400 |
| Data rows | 5000 | `Import:MaxRecords` | 400 |
| Columns, and row index of any cell | 256 columns, `Import:MaxRecords` plus the header row and 100 rows | `Import:MaxColumns`, `Import:MaxRecords` | 400, before the sheet is read |
| Run time | 120 seconds | `Import:MaxSeconds` | 408, rolled back |

## Crash and repeat runs

The import is one database transaction. A crash, a timeout or a dropped connection midway commits
nothing, so there is no half import to clean up. Rows are committed together at the end.

A second run creates the entries again. The file is not remembered. Run with `dryRun` first, and put
a uniqueness rule on the type if repeats must be refused; a row that repeats a unique value is
reported as a row error.

## Not covered yet

The file is sent with the mapping, not kept from analyze under an id. Sheets past the row limit run
as several imports of split files, not as background work with progress.

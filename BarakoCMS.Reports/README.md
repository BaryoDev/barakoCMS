# BarakoCMS.Reports

Group, count and sum a tenant's own content. A report is a saved definition: a content type,
filters, one group-by field and measures (count, sum, avg, min, max).

## Install

```sh
dotnet add package BarakoCMS.Reports
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=Reports`). To name it by hand, put
`modules.Add(new BarakoCMS.Reports.ReportsModule())` in the `AddBarakoCMS` callback. See `MODULES.md`
in the repository.

## Endpoints

| Route | What it does |
|---|---|
| `POST /api/reports` | Save a report definition. Needs `manage_reports` (Admin by default). |
| `GET /api/reports` | List definitions, paginated. Needs `manage_reports`. |
| `GET /api/reports/{name}?from=&to=&format=csv` | Run a report as the caller. JSON, or CSV with `format=csv`. |

## Access

A report runs as the person who asks. Their row conditions narrow what is counted. A group,
measure or filter on a field they cannot read is refused with 403, and entries whose Sensitivity
they may not see are left out. A role whose rules cannot be written as a database condition is
refused. The group count is capped by the report's `groupLimit` (default 100, at most 1000).

The CSV export prefixes a cell that starts with `=`, `+`, `-` or `@` with a single quote so a
spreadsheet does not run it as a formula.

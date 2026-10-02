<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Import logo" />
  <h1>BarakoCMS.Import</h1>
  <p><em>Optional bulk-import module for barakoCMS — .xlsx &amp; CSV into content.</em></p>
</div>

---

A drop-in **module** for [barakoCMS](https://github.com/BaryoDev/barakoCMS) that turns spreadsheet
uploads into content. It parses `.xlsx`/CSV with the zero-dependency
[Talaan](https://github.com/BaryoDev/Talaan) reader, then bulk-creates content items through the
CMS's own validation, permissions, and event-sourcing.

## Enable it

```sh
dotnet add package BarakoCMS.Import
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Import`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Import.ImportModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

No services or schema to configure — the module only contributes endpoints.

## The two-step flow

1. **Analyze** — `POST /api/import/analyze` (multipart file upload). Returns a typed preview grid
   (rows × columns, each cell tagged `Text`/`Number`/`Date`/`Boolean`) plus a suggested header row.
   Nothing is stored. A UI uses this to let the user map columns to content fields.

2. **Create** — `POST /api/import/content` (JSON):

   ```json
   {
     "contentType": "member",
     "records": [
       { "FirstName": "Dindo", "LastName": "Abantao", "MemberNo": "3322657" }
     ],
     "continueOnError": false
   }
   ```

   Every record is validated against the content type first. With `continueOnError: false` (default),
   a single invalid row aborts the whole import and returns per-row errors — nothing is written.
   Otherwise valid rows are created and failures reported. **All creates commit in one transaction.**

   Each record goes through the same write path as `POST /api/contents`: a field the caller may not
   see is dropped, the record is validated, and the type's lifecycle hooks run, each row written
   before the next is checked inside one transaction, so a hook sees the rows before it. The rows
   it writes are held until the import commits, so run a large one outside busy hours: a post that
   needs the same row, such as the journal entry number sequence, waits and may need a retry. One request creates
   at most 5,000 records (`Import:MaxRecords`); past that it answers 400 and writes nothing.

   A cell arrives as text. For a `money` field that declares a currency, plain decimal text (an
   optional sign, digits, at most one point) is stored as a number. Anything else in that column,
   such as `1,250.00`, or an amount with more decimal places than the currency has, is refused for
   that row naming the field. A money field with no currency stores the cell as it always did.

## Why split analyze from create

The mapping and any cleanup (skipping title/section rows, formatting numbers, choosing which columns
matter) happen in your UI between the two calls. The module stays generic: it parses, and it creates
validated content — it does not hard-code any particular spreadsheet's shape.

## Authorization

`analyze` requires the `analyze_spreadsheets` capability. `content` is gated by the **target content type's own
`create` permission** (via the CMS permission resolver) — so a role that can create `member` content
can import members, and nothing else.

## Requires

barakoCMS, BarakoCMS.Abstractions and Talaan, at the versions NuGet lists as this package's
dependencies. The first two are never older than 4.3.0, the release that added
BarakoCMS.Abstractions. Targets .NET 10.

## License

[MPL-2.0](LICENSE) © BaryoDev

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

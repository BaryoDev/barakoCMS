<div align="center">
  <img src="assets/logo.svg" width="96" height="96" alt="BarakoCMS.Import logo" />
  <h1>BarakoCMS.Import</h1>
  <p><em>Optional bulk-import module for barakoCMS — .xlsx &amp; CSV into content.</em></p>
</div>

---

A drop-in **module** for [barakoCMS](https://github.com/BaryoDev/barakoCMS) that turns spreadsheet
uploads into content. It parses `.xlsx`/CSV with the zero-dependency
[Talaan](https://github.com/BaryoDev/Talaan) reader, then bulk-creates content items through the
CMS's own validation, permissions, and event-sourcing.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Import.ImportModule());
});
```

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

## Why split analyze from create

The mapping and any cleanup (skipping title/section rows, formatting numbers, choosing which columns
matter) happen in your UI between the two calls. The module stays generic: it parses, and it creates
validated content — it does not hard-code any particular spreadsheet's shape.

## Authorization

`analyze` requires an authenticated user. `content` is gated by the **target content type's own
`create` permission** (via the CMS permission resolver) — so a role that can create `member` content
can import members, and nothing else.

## Requires

barakoCMS ≥ 2.2.0 and Talaan. Targets .NET 8.

## License

[MPL-2.0](LICENSE) © BaryoDev

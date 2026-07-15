<div align="center">
  <h1>BarakoCMS.Files</h1>
  <p><em>Optional file-attachment module for barakoCMS.</em></p>
</div>

---

Adds file upload + download to [barakoCMS](https://github.com/BaryoDev/barakoCMS), storing bytes in
Postgres via Marten. Handy for receipts, photos, and documents attached to your own records.

## Enable it

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new BarakoCMS.Files.FilesModule());
});
```

## Endpoints

| Method & path | Purpose |
|---|---|
| `POST /api/files` | Upload one image or PDF (≤ 10 MB, multipart). Returns `{ id, fileName, contentType, size }`. |
| `GET  /api/files/{id}` | Stream the file back with its original content type. Requires a Bearer token. |

Attach the returned `id` to your own documents; fetch it later with the download endpoint. Because
`GET` requires authentication, browser `<img>`/`<a>` tags can't load it directly — fetch it with the
token and use an object URL.

## Notes

Files live in the `stored_files` Marten document (bytes in Postgres). This suits low-to-moderate
volumes of small files; for large-scale blob storage, use an object store instead.

## Requires

barakoCMS ≥ 2.2.0. Targets .NET 8.

## License

[MPL-2.0](LICENSE) © BaryoDev

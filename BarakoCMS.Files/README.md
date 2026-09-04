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
| `GET  /api/files` | List uploads, newest first, paginated. `?q=` matches a substring of the name, `?contentType=image/` a type or prefix. |
| `GET  /api/files/{id}/meta` | The record without the bytes: name, type, size, public flag, alt text, caption. |
| `PATCH /api/files/{id}` | Set `alt` and `caption`. A field left out is unchanged; an empty string clears it. |
| `GET  /api/files/{id}/usage` | The entries whose data references the file, by id or by URL. Paginated. |
| `DELETE /api/files/{id}` | Remove the file, its cached resizes and their bytes. 409 with the first ten usages while an entry references it; `?force=true` deletes anyway. |
| `GET  /api/public/files/{id}` | Anonymous download of a public file. Private files are 404. |
| `GET  /api/public/files/{id}/meta` | Anonymous alt text and caption for a public file. Private files are 404. |

Attach the returned `id` to your own documents; fetch it later with the download endpoint. Because
`GET /api/files/{id}` requires authentication, browser `<img>`/`<a>` tags cannot load it directly:
fetch it with the token and use an object URL, or upload with `isPublic=true` and use the public
route.

Every route except the two public ones is gated on the `upload_files` capability, which the module
grants to Admin at startup. The where-used lookup scans the tenant's entries for the file's id or
its storage key as a substring of any field, so it finds a bare id, a `/api/public/files/{id}` URL
with or without `?w=`, and an object store's public URL. A usage row always carries the entry's id
and status; its title is there only when the caller holds read on the type and the sensitivity
scrub leaves it, the same two checks as `GET /api/contents`.

## Notes

Files live in the `stored_files` Marten document (bytes in Postgres). This suits low-to-moderate
volumes of small files; for large-scale blob storage, use an object store instead.

## Requires

barakoCMS ≥ 4.0.0. Targets .NET 10.

## License

[MPL-2.0](LICENSE) © BaryoDev

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

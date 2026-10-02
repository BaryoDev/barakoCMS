<div align="center">
  <h1>BarakoCMS.Files</h1>
  <p><em>Optional file-attachment module for barakoCMS.</em></p>
</div>

---

Adds file upload + download to [barakoCMS](https://github.com/BaryoDev/barakoCMS), storing bytes in
Postgres via Marten. Handy for receipts, photos, and documents attached to your own records.

## Enable it

```sh
dotnet add package BarakoCMS.Files
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Files`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Files.FilesModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

## Endpoints

| Method & path | Purpose |
|---|---|
| `POST /api/files` | Upload one PNG, JPEG, GIF, WebP, AVIF or PDF (up to 10 MB, multipart). The part's Content-Type must be exactly one of those, and the file must start the way that format does. Returns `{ id, fileName, contentType, size }`. |
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

## Using files from another module

The module implements `IFileStore` from `BarakoCMS.Abstractions`, so the core and other modules can
store, read and delete a file without referencing this package. Every call works in the scope's
tenant.

```csharp
public sealed class Receipts(IFileStore files)
{
    public async Task<Guid?> KeepAsync(Stream pdf, Guid payer, CancellationToken ct)
    {
        var saved = await files.SaveAsync(
            new FileToStore { Content = pdf, FileName = "receipt.pdf", ContentType = "application/pdf", Owner = payer }, ct);
        return saved.File?.Id; // null: saved.Refused says why
    }

    public Task<Stream?> ReadAsync(Guid id, ClaimsPrincipal user, CancellationToken ct) =>
        files.OpenAsync(id, user, ct);
}
```

- `FindPublicAsync`, `OpenPublicAsync` and `PublicUrlAsync` take no caller and answer for public
  files only, the same files `GET /api/public/files/{id}` serves. A private file reads as absent.
  The workflow `Email` action uses these for attachments; see `docs/configuring-email.md`.
  `PublicUrlAsync` gives the object store's URL when the store serves the file itself, and
  otherwise the path `/api/public/files/{id}`.
- `FindAsync` and `OpenAsync` take the signed-in user and hand over what that user could download:
  a public file, or a private one that is theirs or that they administer, the rule
  `GET /api/files/{id}` applies. An API key, a principal that is not signed in and a token for
  another tenant read public files only. Pass the current request's principal: token revocation,
  tenant activity and device trust are the request pipeline's checks and are not run again here.
- `SaveAsync` runs the checks `POST /api/files` runs (type, content, 10 MB, the scanner when
  configured) and writes the record an upload writes, owned by `Owner`, so the routes above serve
  it as they serve an upload. A refused file is not stored, and a scanner refusal is written to the
  audit log.
  It does not check `upload_files`: the calling module decides who may reach it. The file is held
  in memory once while it is checked and stored (its own size from a seekable stream, up to twice
  that from one that is not), and the Postgres storage copies it twice more.
- `DeleteAsync` deletes for a caller `DELETE /api/files/{id}` would delete for, and answers
  `InUse` while an entry names the file unless forced.

`SaveAsync` and `DeleteAsync` commit, through the scope's session. Call them before staging
anything else on it: they throw `InvalidOperationException` when work is already staged, so a
refused save never commits a caller's rows. Inside a content batch nothing commits until the batch
does, and with an object store the bytes do not roll back with it; `MODULES.md` says what that
leaves behind.

There is no member that reads or deletes a private file for no user. A job that runs without one
can store a file and read public files. A file stored with no `Owner` shows the empty id as
`uploadedBy` on `GET /api/files` and `GET /api/files/{id}/meta`.

## Notes

Files live in the `stored_files` Marten document (bytes in Postgres). This suits low-to-moderate
volumes of small files; for large-scale blob storage, use an object store instead.

## Requires

barakoCMS and BarakoCMS.Abstractions, at the versions NuGet lists as this package's dependencies.
Neither is older than 4.3.0, the release that added BarakoCMS.Abstractions. Targets .NET 10.

## License

[MPL-2.0](LICENSE) © BaryoDev

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

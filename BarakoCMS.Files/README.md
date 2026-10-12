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
| `GET  /api/files/{id}/meta` | The record without the bytes: name, type, size, width and height, public flag, alt text, caption. |
| `PATCH /api/files/{id}` | Set `alt` and `caption`. A field left out is unchanged; an empty string clears it. |
| `GET  /api/files/{id}/usage` | The entries whose data references the file, by id or by URL. Paginated. |
| `DELETE /api/files/{id}` | Remove the file, its cached resizes and their bytes. 409 with the first ten usages while an entry references it; `?force=true` deletes anyway. |
| `GET  /api/public/files/{id}` | Anonymous download of a public file. Private files are 404. |
| `GET  /api/public/files/{id}/meta` | Anonymous alt text, caption, width and height for a public file; `?w=` describes the variant at that width, and a width the download would refuse is ignored. Private files are 404. |

Attach the returned `id` to your own documents; fetch it later with the download endpoint. Because
`GET /api/files/{id}` requires authentication, browser `<img>`/`<a>` tags cannot load it directly:
fetch it with the token and use an object URL, or upload with `isPublic=true` and use the public
route.

Every route except the download and the two public ones is gated on the `upload_files` capability.
The download asks only for a signed-in user, and then for the file to be theirs. A private file
somebody else uploaded is downloaded, and deleted, only by a caller holding `manage_all_files`;
`upload_files` alone does not open it. The module grants both capabilities to the seeded Admin
role at startup, SuperAdmin satisfies both, and any role you create can be given either. The
where-used lookup scans the tenant's entries for the file's id or
its storage key as a substring of any field, so it finds the id a `file` field holds, a bare id in any other field, a `/api/public/files/{id}` URL
with or without `?w=`, and an object store's public URL. A usage row always carries the entry's id
and status; its title is there only when the caller holds read on the type and the sensitivity
scrub leaves it, the same two checks as `GET /api/contents`.

## Choosing where files are stored

A tenant can choose where its new uploads go, Postgres or an S3-compatible bucket, without anyone
editing the server or restarting it. The three routes need the `manage_storage` capability, which
SuperAdmin and Admin hold by default:

| Method & path | Purpose |
|---|---|
| `GET  /api/settings/storage` | The saved setting, where uploads go right now (`activeProvider`), whether configuration decides (`readOnly`, `environmentKeys`), and `secretSet` in place of the secret. |
| `PUT  /api/settings/storage` | `{ "provider": "s3", "s3": { "bucket", "region", "serviceUrl", "publicBaseUrl", "usePublicReadAcl", "forcePathStyle", "accessKey", "secretKey" } }`, or `{ "provider": "postgres" }`. Applies from the next upload. |
| `POST /api/settings/storage/test` | Puts, reads back and deletes a small object with the settings sent, saves nothing, and answers `{ ok, step, errorCode, message }` naming the step that failed (`put`, `get` or `delete`) and the provider's own error code. |

- The secret key is write-only. It is stored encrypted with the deployment's secret protector, and
  no response carries it. On `PUT` and on the test, leaving `secretKey` out keeps the stored one and
  an empty string clears it.
- Nothing moves. Each file records where its bytes are, and reads follow that, so files stored in
  Postgres keep downloading after a switch to a bucket, and the other way round. Switching back to
  Postgres keeps the bucket's settings so its files stay readable.
- A file records that it is in a bucket, not which bucket, so while any file is in the tenant's
  bucket a `PUT` that changes the bucket, region or service URL, or clears the secret, is refused
  with a 409. The provider can still be switched, and the keys replaced with new values. A bucket
  set later in configuration takes over reading every tenant's bucket files.
- If the stored secret cannot be decrypted (the deployment's key changed), `GET` reports
  `secretUnreadable: true` and new uploads go to Postgres until the secret is sent again.
- Configuration wins. If the deployment sets any `Files:S3` key (`Files__S3__Bucket` and so on),
  that storage serves every tenant, the API's setting is read-only, and `PUT` answers 409 naming
  the keys. A deployment configured that way behaves exactly as before.
- A new install still stores files in Postgres, and a tenant that saves nothing stays there.
- A bucket address typed into the API is one the server will call, so by default it may only be a
  public address: loopback, private networks and cloud metadata addresses are refused when the
  connection opens, as for webhooks. A deployment whose object store sits on its own private
  network sets `Modules__Files.S3__AllowPrivateServiceUrl=true`.
- Choosing a bucket needs the `BarakoCMS.Files.S3` module installed. Without it only `postgres` is
  offered (`availableProviders`).

On an existing database, run `migrations/5.0.0/files-storage-settings.sql` (or `db-migrate`)
before the upgrade, so `db-assert` finds the new table.

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
  a public file, or a private one that is theirs or that `manage_all_files` opens for them, the
  rule `GET /api/files/{id}` applies. An API key, a principal that is not signed in and a token for
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
- `FindPublicManyAsync` and `FindManyAsync` answer the two finds for many ids in one query. Each
  answer carries the file's `PublicUrl` (null when it is private), `Alt` and `Caption`. The core's
  file fields resolve through these; see `docs/file-fields.md`.

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

[MIT](LICENSE) © BaryoDev

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

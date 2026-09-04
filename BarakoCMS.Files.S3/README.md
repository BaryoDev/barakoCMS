<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Files.S3 logo" />
  <h1>BarakoCMS.Files.S3</h1>
  <p><em>S3-compatible object storage for the barakoCMS Files module.</em></p>
</div>

---

Stores uploads in AWS S3, Cloudflare R2, MinIO, or anything else that speaks the S3 API, instead of
on the application's own disk. Public files get a direct, CDN-friendly URL; private files stay
private and are proxied through the API so authorisation is still checked on every read.

## Enable it

It depends on the Files module, which owns the upload endpoints. The package brings Files in
with it, so one reference is enough; enable both:

```sh
dotnet add package BarakoCMS.Files.S3
```

```csharp
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();
app.UseBarakoCMS();
```

The package reference plus a restart is the install. `AddBarakoCMS` finds every module in the
application's dependency context, and `BarakoCMS:Modules:Enabled` decides which of them run
(`BarakoCMS__Modules__Enabled=Files,Files.S3`). Unset, every referenced module runs and the API logs
one warning saying so. To name it by hand instead, put
`modules.Add(new BarakoCMS.Files.S3.S3FilesModule())`
in the `AddBarakoCMS` callback; discovery skips a type the host already added. See `MODULES.md` in
the repository.

## Configuration

```json
{
  "Files": {
    "S3": {
      "Bucket": "my-bucket",
      "Region": "us-east-1",
      "AccessKey": "...",
      "SecretKey": "...",
      "ServiceUrl": null,
      "ForcePathStyle": false,
      "PublicBaseUrl": null,
      "UsePublicReadAcl": false
    }
  }
}
```

The section is `Files:S3`. With no `Files:S3:Bucket` set the provider stays dormant and the default
storage keeps serving, so a typo here shows up as "uploads still work but nothing reaches the
bucket" rather than as an error.

| Key | Notes |
|---|---|
| `ServiceUrl` | Set for R2 or MinIO; leave null for AWS |
| `ForcePathStyle` | Usually `true` for MinIO |
| `PublicBaseUrl` | Serve public files from your CDN domain |
| `UsePublicReadAcl` | Leave `false` on buckets that block public ACLs, which is the safer default |

Keys belong in environment variables or a secret store, never in a checked-in `appsettings.json`.

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

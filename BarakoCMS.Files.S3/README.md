<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Files.S3 logo" />
  <h1>BarakoCMS.Files.S3</h1>
  <p><em>S3-compatible object storage for the barakoCMS Files module.</em></p>
</div>

---

Stores uploads in AWS S3, Cloudflare R2, or a self-hosted store that speaks the S3 API, instead of
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
| `ServiceUrl` | Set for R2 or a self-hosted store; leave null for AWS |
| `ForcePathStyle` | Usually `true` for self-hosted stores |
| `PublicBaseUrl` | Serve public files from your CDN domain. The key is appended, so a public file's URL is `{PublicBaseUrl}/public/{name}` |
| `UsePublicReadAcl` | Defaults to `true`. Set it to `false` on buckets that block public ACLs, and grant read on `public/*` as below, not on the bucket |

Keys belong in environment variables or a secret store, never in a checked-in `appsettings.json`.

## Key layout and public access

Visibility is in the key. A public file is stored as `public/{name}` and a private one as
`private/{name}`, and a resized copy sits under the prefix for the visibility of the file it came
from. The name is random and the extension comes from the checked content type; the upload's
`isPublic` flag picks the prefix and nothing else from the request is in the key.

That prefix is what makes direct public URLs safe. A bucket policy and a CDN origin grant by bucket
or by key prefix, never by the `isPublic` flag in the database. Granted on the whole bucket, they
serve a private file to anyone who has its key. Granted on `public/*`, they serve public files and
nothing else.

On AWS S3 there are two ways to do that. Neither is run by this module's tests, which check where
objects land and not what a bucket serves; the policies below are the shape AWS documents.

**Direct bucket URLs.** With `UsePublicReadAcl` set to `false`, this is the bucket policy:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "PublicFilesOnly",
      "Effect": "Allow",
      "Principal": "*",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::my-bucket/public/*"
    }
  ]
}
```

Do not grant `s3:GetObject` on `arn:aws:s3:::my-bucket/*`, and do not grant `s3:ListBucket` to
anonymous callers. Set `PublicBaseUrl` to the bucket's URL (`https://my-bucket.s3.eu-west-1.amazonaws.com`).

The policy alone is not enough on a new bucket. AWS turns on all four Block Public Access settings
when a bucket is created, and two of them stop this route: `BlockPublicPolicy` rejects a policy
with `"Principal": "*"` when it is saved, and `RestrictPublicBuckets` refuses anonymous reads even
with such a policy in place. Both have to be off, on the bucket and on the account, for the direct
URL to work. `BlockPublicAcls` and `IgnorePublicAcls` can stay on.

**CloudFront, the one to prefer.** It needs none of the four settings changed: the bucket stays
private and only the distribution reads the prefix. Create the distribution with the bucket as its
origin and an origin access control, leave the origin path empty so the URL path is the key (an
origin path of `/public` would look for `public/public/{name}`), and scope the bucket policy to the
distribution and the prefix:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "CloudFrontPublicFilesOnly",
      "Effect": "Allow",
      "Principal": { "Service": "cloudfront.amazonaws.com" },
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::my-bucket/public/*",
      "Condition": {
        "StringEquals": {
          "AWS:SourceArn": "arn:aws:cloudfront::111122223333:distribution/EDFDVBD6EXAMPLE"
        }
      }
    }
  ]
}
```

Set `PublicBaseUrl` to the distribution's domain and `UsePublicReadAcl` to `false`. A request for
`/private/...` through the distribution is refused by S3, because the policy does not cover it.

Where the only grant on offer covers the whole bucket (an R2 bucket made public in the dashboard
is public as a whole), there is no safe way to give public files a direct URL from the same bucket as private
ones. Leave `PublicBaseUrl` unset there and public files are streamed through
`GET /api/public/files/{id}`.

The storage refuses a write whose key and visibility disagree: a private object under `public/`, or
a public one under `private/`. The key is read loosely for that check, so `/public/x`,
`./public/x`, `a/../public/x`, `public\x` and `Public/x` are refused for a private object too. A
host writing its own `IFileStorage` can ask the same question with `FileKeys.Contradicts`.

### Files stored before the prefixes

A file uploaded before BarakoCMS.Files 4.4.0 keeps the key on its row, at the bucket root
with no prefix, and nothing moves it. The API reads, resizes and deletes it by that key as before,
and a new resize of it is stored under the prefix for its visibility.

Those files are the reason not to narrow an existing bucket's grant without looking first. A grant
on `public/*` does not cover a root key, so an old private file stops being reachable at the
bucket, which is the point, and so does an old public one: its stored `publicUrl` then answers 403,
and `GET /api/public/files/{id}` still redirects to it. Upload those public files again before
narrowing the grant. While the grant covers the whole bucket, old and new private files alike are
readable by anyone who has a key.

## S3-compatible stores

| Store | Notes |
|---|---|
| AWS S3 | Leave `ServiceUrl` null and set `Region`. `UsePublicReadAcl` only works on a bucket with ACLs enabled, and new buckets have them disabled, so prefer CloudFront or the policy on `public/*` above. |
| Cloudflare R2 | No object ACLs, so set `UsePublicReadAcl` to `false`. A bucket made public in the R2 dashboard is public as a whole, private files included: see "Key layout and public access". |
| [SeaweedFS](https://github.com/seaweedfs/seaweedfs) | Apache-2.0. The module's own tests run against it. Does not apply an ACL sent with an upload, so set `UsePublicReadAcl` to `false`. Anonymous read granted on the whole bucket covers private files too: see "Key layout and public access". |
| [Garage](https://garagehq.deuxfleurs.fr/) | AGPL-3.0. No object ACLs, so treat it like R2 and set `UsePublicReadAcl` to `false`. |
| MinIO | Unmaintained. The upstream repository is archived and gets no security patches, so it is not recommended for new deployments. |

## Part of barakoCMS

This is an optional module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.

Contributions are welcome — including a module icon or other design work. See
[CONTRIBUTING.md](https://github.com/BaryoDev/barakoCMS/blob/master/CONTRIBUTING.md).

Licensed under MPL-2.0.

If barakoCMS is useful to you, a star on the [repository](https://github.com/BaryoDev/barakoCMS) helps other people find it.

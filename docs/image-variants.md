# Image variants: resizing on request

Both download routes take a `?w=` and answer with a narrower copy of the image.

```
GET /api/public/files/{id}?w=640     anonymous, public files only
GET /api/files/{id}?w=640            authenticated, same rules as without the parameter
```

Leave `?w=` off and you get exactly what you got before this existed: the file, unchanged.

## What a width actually gets you

Requests are snapped onto a ladder rather than honoured literally. `?w=400` is answered at 640,
`?w=640` at 640, `?w=700` at 960. The ladder is:

```
160  320  640  960  1280  1920   and the cap itself
```

That is not a rounding convenience, it is the reason the cache is safe to have. Honouring an
arbitrary width means an anonymous caller can walk `?w=1` through `?w=2048` on one public image and
leave two thousand stored blobs behind. Snapping bounds it at one stored variant per rung, so the
worst an attacker gets out of a public image is seven of them.

An image already narrower than the rung is served unchanged. Nothing is ever upscaled.

## The cap

`?w=` above the cap is refused with a 400 that names the cap. The default is 2048.

```json
{
  "Files": {
    "Images": {
      "MaxWidth": 2048,
      "MaxSourcePixels": 50000000
    }
  }
}
```

Or as environment variables:

```
Files__Images__MaxWidth=2048
Files__Images__MaxSourcePixels=50000000
```

`MaxWidth` set to `0` turns variants off. A `?w=` is then ignored and the original is served, which
is the answer every one of these URLs gave before variants existed.

`MaxSourcePixels` is the other limit, and it is the one people forget. The upload cap is ten
megabytes of *compressed* bytes, and a ten megabyte PNG can decode to a bitmap of tens of gigabytes.
Dimensions are read from the header before a single pixel is decoded, and an image over the limit is
served at full size rather than resized. Nobody is refused; the server just declines to do the work.

## What gets resized

PNG, JPEG and WebP. Everything else is served unchanged, including with a `?w=` on it, so a frontend
that appends the parameter to every asset URL does not break on the one that is a PDF.

GIF is left out because resizing an animated one resamples every frame, which makes the cost of a
request a property of the file rather than of the requested width, on an anonymous route. AVIF is
left out because ImageSharp has no AVIF decoder. Both are served at full size.

Bytes that do not decode are served unchanged too. A download does not fail because a resize did.

## Access rules

A variant is reachable exactly when its original is, and there is nothing to configure about that.

- The access check runs on the original, before any resizing is considered. A private file with a
  `?w=` on the public route is a 404 and no work is done.
- A cached variant is stored as its own `StoredFile` row pointing at its parent through
  `ParentFileId`, and **that row is not addressable**. Both routes return 404 for an id whose
  `ParentFileId` is set, including for an admin who could read the original.

The second point is the design rather than an extra precaution. If a variant were addressable it
would need access rules of its own, and a second copy of an access rule is a copy that can drift out
of step with the file it came from. There is one record whose readability is ever decided and it is
the original.

## Where the bytes go

Through the same `IFileStorage` as the upload, with the same public-ness, under a key derived from
the original's (`abc123.png` becomes `abc123_w640.png`). On S3 that means a variant of a public
object gets its own public URL and the route redirects to it; on Postgres the bytes are proxied like
any other.

Variants are made on the first request that asks for them and kept. They are not counted against
anything, and there is no eviction: seven rungs per image is a bounded amount of storage, and the
cost of keeping them is far below the cost of recomputing them.

## What is not here

There is no cropping, no format conversion, no quality parameter and no `?h=`. Each of those is a
new axis on the cache key, and the number of stored variants is the product of the axes. If one is
worth adding, it is worth adding with the same ladder treatment.

Deleting an original does not delete its variants, because nothing deletes a file yet: the module
has no delete endpoint. When one arrives it has to sweep `ParentFileId`.

`ParentFileId` is indexed on a database created after this shipped. It is not indexed on an upgraded
one: the app runs Marten with `AutoCreate.CreateOnly`, which creates a table that is missing and
never alters one that already exists, and `stored_files` exists on every deployed instance. Nothing
in the running code queries by it, so there is nothing slow today, but the sweep above needs it.
Apply `migrations/4.2.0/stored-files-parent-index.sql` before writing that endpoint.

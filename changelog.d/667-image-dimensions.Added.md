- **A frontend had to download an image to learn its size.** The Files module reads an image's
  width and height from its header on upload (PNG, JPEG, GIF, WebP, as shown after any EXIF turn)
  and returns `width` and `height` from the upload, `GET /api/files/{id}/meta` and
  `GET /api/public/files/{id}/meta`. A variant records its own size, and `?w=` on the public
  metadata route answers for the copy served at that width. Files uploaded before this have none.
  `BarakoCMS.Files` is 5.0.0.

- **An upload's declared type was matched as a prefix and never compared with the file.**
  `POST /api/files` now parses the declared type and requires it to be exactly one of PNG, JPEG,
  GIF, WebP, AVIF or PDF, and requires the start of the file to be that format. A mismatch is 400.
  The type is stored as the bare lower case name, so `IMAGE/PNG` is kept as `image/png`.
  `GET /api/files/{id}` now sends the same sandboxing Content-Security-Policy as the public
  download route. BarakoCMS.Files 4.3.1.

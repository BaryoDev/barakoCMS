- **An upload's declared type was matched as a prefix and never compared with the file.**
  `POST /api/files` now parses the declared type and requires it to be exactly one of PNG, JPEG,
  GIF, WebP, AVIF or PDF, and requires the start of the file to be that format. A mismatch is 400.
  The type is stored as the bare lower case name, so `IMAGE/PNG; x=y` is kept as `image/png`, and
  the storage key's extension comes from that type rather than the uploaded file name. Both
  download routes send a sandboxing Content-Security-Policy, and redirect to an object store only
  for a file stored with one of those exact types; any other stored file is streamed through the
  API as `application/octet-stream`. The redirect also answers 302 now; it used to answer 204.
  BarakoCMS.Files 4.3.1.

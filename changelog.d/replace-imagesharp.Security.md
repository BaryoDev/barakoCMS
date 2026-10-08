- **Image variants are made with SkiaSharp instead of SixLabors.ImageSharp.** BarakoCMS.Files 4.5.0
  resizes PNG, JPEG and WebP with SkiaSharp 4.153.1 (MIT, over BSD-licensed Skia) and its Linux
  build that needs only libc and libstdc++, on x64 and arm64. Formats, the width ladder, never
  upscaling and the pixel limit read from the header before decoding are unchanged. A variant no
  longer carries the original's EXIF; a sideways photo is turned upright in its pixels instead. An
  animated WebP is served at full size rather than resized. `ImageSharpResizer` still compiles and
  now delegates to `SkiaImageResizer`; it is marked obsolete for removal in BarakoCMS.Files 5.0.
  The suite image keeps only the Skia native library for its own architecture.

- **Image variants are made with SkiaSharp instead of SixLabors.ImageSharp.** BarakoCMS.Files 4.5.0
  resizes PNG, JPEG and WebP with SkiaSharp 4.153.1 (MIT, over BSD-licensed Skia) and its Linux
  build that needs only libc and libstdc++, on x64 and arm64. The width ladder, never upscaling,
  keeping the original's format and the pixel limit read from the header before decoding are
  unchanged. What a variant looks like changes in these ways:
  - EXIF is no longer copied into a variant, so camera details and location stay with the original.
  - A photo with an EXIF orientation tag is turned upright in the variant's pixels instead.
  - An animated WebP is served whole at full size rather than resized. The pixel limit still runs on
    its header first.
  - An animated PNG's variant is a still of its first frame.
  - JPEG and lossy WebP variants are written at quality 75 rather than at a quality estimated from
    the source. A lossless WebP stays lossless.

  `ImageSharpResizer` still compiles and now delegates to `SkiaImageResizer`; it is marked obsolete
  for removal in barakoCMS 5.0. The suite image keeps only the Skia native library for its own
  architecture. That library bundles code under several licences, and their notices ship in the
  suite image under `/app/licenses`. Resizing also declines bytes that are not the stored file's
  declared type, or not PNG, JPEG or WebP, before any decoder sees them. A host that takes BarakoCMS.Files from NuGet and publishes without a runtime
  identifier gets Skia's native library for every platform it supports, several hundred megabytes
  with the Windows debug symbols; publish with `-r linux-x64` (or your target) to keep one.

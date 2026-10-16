# Third-party notices

barakoCMS depends on other people's work. Every NuGet dependency, with its exact version, is in
the `packages.lock.json` beside each project, and each package carries its own licence on
nuget.org. This page covers the cases where a licence asks something of you, or changed under
you, so they are not left for you to find in a lock file.

## SixLabors.ImageSharp (no longer used)

**BarakoCMS.Files 4.5.0 and later do not use ImageSharp.** They make image variants with SkiaSharp,
below. Nothing in this repository or in the published images references ImageSharp today.

**BarakoCMS.Files up to 4.4.0** depended on SixLabors.ImageSharp 3.1.12. From 3.0, ImageSharp is not
plain Apache-2.0 as it was before: it is under the
[Six Labors Split License 1.0](https://github.com/SixLabors/ImageSharp/blob/v3.1.12/LICENSE). Under
it, ImageSharp is licensed to you under Apache-2.0 when any one of these holds, and otherwise needs
a Six Labors commercial licence:

- you use it in software under an open source or source available licence;
- you consume it as a transitive package dependency, installed by a package not related to Six
  Labors, which is how BarakoCMS.Files brought it in;
- you reference it directly as a for-profit company or individual with less than 1M USD annual
  gross revenue, or as a non-profit or registered charity.

So a host that took ImageSharp only through BarakoCMS.Files 4.4.0 or earlier consumed it as a
transitive dependency. A host that also references ImageSharp directly in its own project is a
direct consumer and should check the terms above for itself. Upgrading to BarakoCMS.Files 4.5.0 or
later removes the question.

## SkiaSharp and the native Skia library

BarakoCMS.Files resizes images with
[SkiaSharp](https://github.com/mono/SkiaSharp) (MIT) and its Linux native build
`SkiaSharp.NativeAssets.Linux.NoDependencies`, over Google's Skia (BSD-3-Clause). The native library
bundles code under several more licences, including Apache-2.0, the IJG licence, zlib and FreeType.
Their notices travel with the binary: the suite image ships them under `/app/licenses`, and the
NuGet package ships them as `THIRD-PARTY-NOTICES.txt`.

## Lib.Net.Http.WebPush

BarakoCMS.Push sends Web Push messages with
[Lib.Net.Http.WebPush](https://github.com/tpeczek/Lib.Net.Http.WebPush) 3.3.1 (MIT). The package
declares MIT as its licence expression and does not ship a licence file; its copyright line, read
from the package's nuspec, is "Copyright © 2018 - 2025 Tomasz Pęczek".

## barakoCMS itself

The core (`barakoCMS/`, `BarakoCMS.Abstractions/`) is MPL-2.0 and every other package is MIT, as the
[README](README.md#license) and D24 in [DECISIONS.md](DECISIONS.md) say.

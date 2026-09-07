- **New icons for the fourteen module packages.** Each keeps the ground colour it already had, with a
  white glyph and the bean device in the lower right, so a package stays recognisable in a NuGet
  search result while the set reads as a family. `BarakoCMS.Templates` and `BarakoCMS.Testing` are
  tooling rather than feature modules and keep the icons they had. `Directory.Build.props` already
  packs each project's `assets/icon.png` as its `PackageIcon`, so no packaging wiring changed.

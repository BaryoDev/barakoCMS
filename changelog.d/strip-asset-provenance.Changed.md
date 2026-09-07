- **Image assets ship without embedded provenance metadata.** Design tools stamp C2PA content
  credentials into what they export, naming the tool that produced the file, and
  `Directory.Build.props` packs `assets/icon.png` into every module package, so an unstripped export
  would have carried that stamp to nuget.org. `scripts/strip-asset-provenance.py` removes it by
  filtering the optional PNG chunks and the SVG `<metadata>` element, which leaves the image data
  byte for byte identical rather than re-encoding it. `scripts/preflight.sh` now fails if any asset
  still carries a stamp.

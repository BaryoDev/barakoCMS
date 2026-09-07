#!/usr/bin/env python3
"""Strip embedded provenance metadata from image assets.

Design tools now stamp C2PA content credentials into what they export. In a PNG
that is a caBX ancillary chunk, in an SVG a <metadata><c2pa:manifest> element
holding a signed base64 blob. It names the tool that made the file and ships
wherever the file ships, which for these icons is inside every public NuGet
package, since Directory.Build.props packs assets/icon.png as PackageIcon.

We do not publish tool attribution, so the assets are stripped before they land.

Both formats treat this data as optional, so removing it leaves the artwork
untouched: the PNG keeps its IDAT stream byte for byte, the SVG keeps every
drawing element. Nothing is re-encoded.

    strip-asset-provenance.py <dir>            strip in place, report what went
    strip-asset-provenance.py <dir> --check    exit 1 if anything is still stamped
"""

import os
import pathlib
import re
import struct
import sys

# Optional PNG chunks that carry provenance or free text. Ancillary by spec, so
# a decoder is required to skip them and dropping them cannot change the image.
#
# iCCP is here because a macOS screen capture embeds the display's own colour
# profile, and that profile names an Apple build identifier for the machine that
# took it. Every asset in this repository is plain sRGB artwork or a UI
# screenshot, so none of them need embedded colour management. A project that
# ships colour-critical images should take iCCP back out of this set.
PNG_DROP = {b"caBX", b"jUMB", b"jumb", b"iTXt", b"tEXt", b"zTXt", b"eXIf", b"iCCP"}
PNG_SIG = b"\x89PNG\r\n\x1a\n"

# Substrings that mean a file still carries an attribution stamp.
MARKERS = ("c2pa", "jumd", "anthropic", "claim_generator", "contentcredentials")


def strip_png(path, check):
    raw = pathlib.Path(path).read_bytes()
    if not raw.startswith(PNG_SIG):
        return []
    kept, found, i = [PNG_SIG], [], len(PNG_SIG)
    while i < len(raw):
        length = struct.unpack(">I", raw[i:i + 4])[0]
        kind = raw[i + 4:i + 8]
        end = i + 12 + length
        if kind in PNG_DROP:
            found.append("%s (%d bytes)" % (kind.decode("latin1"), length))
        else:
            kept.append(raw[i:end])
        i = end
        if kind == b"IEND":
            break
    if found and not check:
        pathlib.Path(path).write_bytes(b"".join(kept))
    return found


def strip_svg(path, check):
    text = pathlib.Path(path).read_text(encoding="utf-8")
    out = re.sub(
        r"<metadata>(?:(?!</metadata>).)*?c2pa(?:(?!</metadata>).)*?</metadata>",
        "", text, flags=re.S)
    out = re.sub(r"<c2pa:manifest>.*?</c2pa:manifest>", "", out, flags=re.S)
    out = re.sub(r'\s+xmlns:c2pa="[^"]*"', "", out)
    if out == text:
        return []
    if not check:
        pathlib.Path(path).write_text(out, encoding="utf-8")
    return ["c2pa manifest (%d bytes)" % (len(text) - len(out))]


def still_stamped(path):
    blob = pathlib.Path(path).read_bytes().lower()
    return [m for m in MARKERS if m.encode() in blob]


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    check = "--check" in sys.argv
    hits = 0
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames
                       if d not in {".git", "bin", "obj", "node_modules"}]
        for name in sorted(filenames):
            if not name.lower().endswith((".png", ".svg")):
                continue
            path = os.path.join(dirpath, name)
            found = strip_png(path, check) if name.lower().endswith(".png") \
                else strip_svg(path, check)
            leftover = still_stamped(path)
            if found or leftover:
                hits += 1
                rel = os.path.relpath(path, root)
                if check:
                    print("provenance metadata in %s: %s"
                          % (rel, ", ".join(found or leftover)))
                else:
                    print("stripped %s: %s" % (rel, ", ".join(found)))
                    if leftover:
                        print("  WARNING: still matches %s" % ", ".join(leftover))
    if check and hits:
        print("\n%d asset(s) carry provenance metadata. "
              "Run scripts/strip-asset-provenance.py to remove it." % hits)
        return 1
    print("no provenance metadata in image assets" if check
          else "stripped %d asset(s)" % hits)
    return 0


if __name__ == "__main__":
    sys.exit(main())

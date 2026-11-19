#!/usr/bin/env python3
"""Prints "<uncompressed_bytes> <compressed_bytes>" for a `docker save` archive.

Both numbers come from the layers in the archive, so they do not depend on the image store of the
Docker host that saved it (a containerd store reports compressed sizes through `docker image
inspect`, and saves layers that are already gzipped).

  uncompressed  the layers unpacked
  compressed    the layers gzipped, about what a registry pull transfers

A gzip layer counts its blob size as compressed and its decompressed size as uncompressed. A plain
tar layer counts its size as uncompressed and its size after gzip level 6 as compressed. A zstd
layer fails the run: the standard library cannot read it, and a wrong number is worse than none.

Usage: perf-image-size.py <docker save tar>
"""

import json
import sys
import tarfile
import zlib

CHUNK = 1024 * 1024


def die(message):
    print(f"perf-image-size: {message}", file=sys.stderr)
    sys.exit(1)


def measure_layer(name, fileobj, size):
    """Returns (uncompressed, compressed) for one layer, reading it in chunks."""
    magic = fileobj.read(4)
    rest = lambda: iter(lambda: fileobj.read(CHUNK), b"")

    if magic[:2] == b"\x1f\x8b":
        decomp = zlib.decompressobj(31)
        raw = len(decomp.decompress(magic))
        for chunk in rest():
            raw += len(decomp.decompress(chunk))
        raw += len(decomp.flush())
        return raw, size

    if magic == b"\x28\xb5\x2f\xfd":
        die(f"layer {name} is zstd compressed, which python3 cannot decompress")

    comp = zlib.compressobj(6, zlib.DEFLATED, 31)
    packed = len(comp.compress(magic))
    for chunk in rest():
        packed += len(comp.compress(chunk))
    packed += len(comp.flush())
    return size, packed


def main():
    if len(sys.argv) != 2:
        die("usage: perf-image-size.py <docker save tar>")
    try:
        archive = tarfile.open(sys.argv[1], "r:")
    except (OSError, tarfile.TarError) as err:
        die(f"cannot read {sys.argv[1]}: {err}")

    with archive:
        try:
            member = archive.getmember("manifest.json")
        except KeyError:
            die("the archive has no manifest.json")
        try:
            manifest = json.load(archive.extractfile(member))
        except (ValueError, OSError) as err:
            die(f"manifest.json is not readable JSON: {err}")

        layers = []
        for entry in manifest if isinstance(manifest, list) else []:
            layers.extend(entry.get("Layers") or [])
        if not layers:
            die("manifest.json names no layers")

        uncompressed = compressed = 0
        for name in layers:
            try:
                info = archive.getmember(name)
            except KeyError:
                die(f"manifest.json names layer {name}, which is not in the archive")
            fileobj = archive.extractfile(info)
            if fileobj is None:
                die(f"layer {name} is not a file in the archive")
            with fileobj:
                raw, packed = measure_layer(name, fileobj, info.size)
            uncompressed += raw
            compressed += packed

    print(uncompressed, compressed)


main()

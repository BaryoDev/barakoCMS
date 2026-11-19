#!/usr/bin/env bash
#
# Proves scripts/perf-image-size.py reads both image sizes from the layers of a docker save archive:
# a gzipped layer and a plain layer built here, so the expected numbers are known. No Docker needed.
#
#   bash scripts/test-perf-image-size.sh

set -uo pipefail

cd "$(dirname "$0")/.."

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

pass=0
fail=0

check() { # <description> <ok|bad>
    if [ "$2" = ok ]; then echo "  ok    $1"; pass=$((pass + 1)); else echo "  FAIL  $1"; fail=$((fail + 1)); fi
}

# Writes good.tar (one gzipped layer, one plain layer), missing.tar (a manifest naming a layer that
# is not there) and empty.tar (no layers), and prints the expected "<uncompressed> <compressed>".
expected=$(python3 - "$tmp" <<'PY'
import gzip, io, json, os, sys, tarfile

tmp = sys.argv[1]
gz_raw = os.urandom(3000) + b"A" * 200000
plain_raw = b"B" * 150000 + os.urandom(500)

def tar_bytes(raw):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as t:
        info = tarfile.TarInfo("file")
        info.size = len(raw)
        t.addfile(info, io.BytesIO(raw))
    return buf.getvalue()

gz_layer = tar_bytes(gz_raw)
plain_layer = tar_bytes(plain_raw)
gz_blob = gzip.compress(gz_layer, 6)

def archive(path, files, layers):
    with tarfile.open(path, "w") as t:
        manifest = json.dumps([{"Config": "c.json", "RepoTags": ["x:1"], "Layers": layers}]).encode()
        for name, data in [("manifest.json", manifest)] + files:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            t.addfile(info, io.BytesIO(data))

archive(os.path.join(tmp, "good.tar"),
        [("blobs/gz.tar.gz", gz_blob), ("blobs/plain.tar", plain_layer)],
        ["blobs/gz.tar.gz", "blobs/plain.tar"])
archive(os.path.join(tmp, "missing.tar"), [("blobs/gz.tar.gz", gz_blob)],
        ["blobs/gz.tar.gz", "blobs/nope.tar"])
archive(os.path.join(tmp, "empty.tar"), [], [])

uncompressed = len(gz_layer) + len(plain_layer)
compressed = len(gz_blob) + len(gzip.compress(plain_layer, 6))
print(uncompressed, compressed)
PY
)

got=$(python3 scripts/perf-image-size.py "$tmp/good.tar" 2>&1)
status=$?
if [ "$status" -eq 0 ] && [ "$got" = "$expected" ]; then
    check "a gzipped and a plain layer give $expected" ok
else
    echo "        expected '$expected', got '$got' (exit $status)"
    check "a gzipped and a plain layer give the expected sizes" bad
fi

out=$(python3 scripts/perf-image-size.py "$tmp/missing.tar" 2>&1); status=$?
if [ "$status" -ne 0 ] && printf '%s\n' "$out" | grep -qF "blobs/nope.tar"; then
    check "a manifest naming a missing layer fails" ok
else
    echo "        exit $status: $out"
    check "a manifest naming a missing layer fails" bad
fi

out=$(python3 scripts/perf-image-size.py "$tmp/empty.tar" 2>&1); status=$?
if [ "$status" -ne 0 ]; then check "a manifest with no layers fails" ok; else check "a manifest with no layers fails" bad; fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]

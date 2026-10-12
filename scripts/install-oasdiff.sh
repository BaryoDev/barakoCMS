#!/usr/bin/env bash
#
# Installs the pinned oasdiff into a directory, checksum verified before it is extracted.
#
# Same shape as the gitleaks install in ci.yml: an unauthenticated binary must not run inside a job
# holding a token, so the archive is checked against the release's checksums.txt values pinned here.
#
# Usage: scripts/install-oasdiff.sh [directory, default /usr/local/bin]

set -euo pipefail

DEST="${1:-/usr/local/bin}"
VERSION=1.33.0

case "$(uname -m)" in
    x86_64|amd64) ARCH=amd64; SHA256=43a4e328e2d13ba1552d760aa68d2485c75c5621f309f6ff64ae895188345247 ;;
    aarch64|arm64) ARCH=arm64; SHA256=4ae3c362d6074d919aada2dea82d0ee84366591600384455d0bdc658ddf8f7ae ;;
    *) echo "install-oasdiff: no pinned checksum for $(uname -m)" >&2; exit 1 ;;
esac

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
curl -sSL --fail -o "$TMP/oasdiff.tar.gz" \
    "https://github.com/oasdiff/oasdiff/releases/download/v${VERSION}/oasdiff_${VERSION}_linux_${ARCH}.tar.gz"
echo "${SHA256}  $TMP/oasdiff.tar.gz" | sha256sum -c -
tar -xzf "$TMP/oasdiff.tar.gz" -C "$TMP" oasdiff
install -m 0755 "$TMP/oasdiff" "$DEST/oasdiff"
"$DEST/oasdiff" --version

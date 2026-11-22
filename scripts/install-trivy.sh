#!/usr/bin/env bash
# Installs a pinned Trivy and checks the download against a checksum written here, taken from the
# release's own checksums file. Verified before extracting, as the gitleaks install is.
#
#   bash scripts/install-trivy.sh [<bin-dir>]     default /tmp/trivy-bin
#
# Adds the directory to GITHUB_PATH when that is set. To move to a new version, change VERSION and
# both checksums together.
set -euo pipefail

VERSION=0.76.2
SHA_AMD64=bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf
SHA_ARM64=ab9d51f86e6a5fb936f605faff6b593ac51bc378fff4d3e8b9d58358b9c367c3

dir=${1:-/tmp/trivy-bin}

case "$(uname -m)" in
    x86_64) arch=64bit; sha=$SHA_AMD64 ;;
    aarch64|arm64) arch=ARM64; sha=$SHA_ARM64 ;;
    *) echo "::error::no Trivy build for $(uname -m)"; exit 1 ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
file="trivy_${VERSION}_Linux-${arch}.tar.gz"
curl -sSL --fail -o "$tmp/$file" "https://github.com/aquasecurity/trivy/releases/download/v${VERSION}/${file}"
echo "${sha}  $tmp/$file" | sha256sum -c -
mkdir -p "$dir"
tar -xzf "$tmp/$file" -C "$dir" trivy
"$dir/trivy" --version | head -1
if [ -n "${GITHUB_PATH:-}" ]; then
    echo "$dir" >> "$GITHUB_PATH"
fi

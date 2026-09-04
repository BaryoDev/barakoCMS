#!/usr/bin/env bash
# Refuses a published image tag that does not serve exactly the expected platforms.
#
# Publishing a single-architecture image is invisible to everyone except whoever pulls it on the
# wrong hardware, and they find out with `no matching manifest` rather than a message about
# architectures (#394). The check reads the pushed manifest list, not the build config, because the
# config being right is not evidence that the push was.
#
#   bash scripts/check-image-platforms.sh <image:tag> [expected-platforms]
#
# expected-platforms is a comma-separated list, default linux/amd64,linux/arm64. Exit 1 on a
# mismatch or when the manifest cannot be read. Attestation manifests (os "unknown") are ignored.
set -euo pipefail

IMAGE="${1:-}"
EXPECTED="${2:-linux/amd64,linux/arm64}"

if [ -z "$IMAGE" ]; then
    echo "usage: $0 <image:tag> [expected-platforms]" >&2
    exit 2
fi

EXPECTED=$(printf '%s' "$EXPECTED" | tr ',' '\n' | sort | paste -sd, -)

PLATFORMS=$(docker buildx imagetools inspect --raw "$IMAGE" \
    | jq -r '[.manifests[]? | select(.platform.os != "unknown") | "\(.platform.os)/\(.platform.architecture)"] | sort | join(",")') \
    || { echo "::error::could not inspect $IMAGE"; exit 1; }

# Empty means the reference is a single image manifest rather than a list, or the list held only
# attestations. Anything outside the platform alphabet means jq was handed something unexpected.
case "$PLATFORMS" in
    ''|*[!a-z0-9/,]*) echo "::error::could not read platforms for $IMAGE, got '$PLATFORMS'"; exit 1 ;;
esac

echo "$IMAGE: $PLATFORMS"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "$IMAGE: $PLATFORMS" >> "$GITHUB_STEP_SUMMARY"
fi

if [ "$PLATFORMS" != "$EXPECTED" ]; then
    echo "::error::$IMAGE serves '$PLATFORMS', expected '$EXPECTED'"
    exit 1
fi

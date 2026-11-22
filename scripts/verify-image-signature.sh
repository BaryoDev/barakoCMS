#!/usr/bin/env bash
# Verifies a published barakoCMS image before you run it.
#
#   bash scripts/verify-image-signature.sh ghcr.io/baryodev/barako-cms:6.0.0
#
# Resolves the reference to a digest, then checks two things against that digest: the cosign
# signature, and the build provenance attestation. Both must come from this repository's release
# workflow (GitHub OIDC, keyless). Release images are signed by release.yml. The :master image is
# signed by publish-master-image.yml, so verify it with:
#
#   VERIFY_WORKFLOW=publish-master-image.yml bash scripts/verify-image-signature.sh ghcr.io/baryodev/barako-cms:master
#
# Needs cosign and docker (buildx) on PATH. Exit 0 when both checks pass, 1 when one fails, 2 on
# bad usage. Only ghcr.io/baryodev/ images are accepted.
set -euo pipefail

REF="${1:-}"
WORKFLOW="${VERIFY_WORKFLOW:-release.yml}"
ISSUER="https://token.actions.githubusercontent.com"

fail() { echo "error: $*" >&2; exit 1; }

if [ -z "$REF" ]; then
    echo "usage: $0 <ghcr.io/baryodev/image[:tag|@sha256:digest]>" >&2
    exit 2
fi

case "$WORKFLOW" in
    release.yml | publish-master-image.yml) ;;
    *) echo "error: VERIFY_WORKFLOW must be release.yml or publish-master-image.yml" >&2; exit 2 ;;
esac

# Lowercase registry path, no whitespace, no shell syntax. The reference is only ever passed as a
# quoted argument, never through eval.
if ! [[ "$REF" =~ ^ghcr\.io/baryodev/[a-z0-9][a-z0-9._-]*((:[A-Za-z0-9._-]+)|(@sha256:[0-9a-f]{64}))?$ ]]; then
    echo "error: refusing '$REF', only ghcr.io/baryodev/<image>[:tag|@sha256:digest] is accepted" >&2
    exit 2
fi

command -v cosign > /dev/null 2>&1 || fail "cosign is not installed, see https://docs.sigstore.dev/cosign/system_config/installation/"

NAME="${REF%%@*}"
NAME="${NAME%:*}"
if [[ "$REF" == *@sha256:* ]]; then
    DIGEST="${REF##*@}"
else
    command -v docker > /dev/null 2>&1 || fail "docker is needed to resolve a tag to a digest, or pass the image@sha256:digest form"
    DIGEST=$(docker buildx imagetools inspect --format '{{json .Manifest}}' "$REF" | jq -r '.digest') \
        || fail "could not read the digest of $REF"
fi
[[ "$DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "no usable digest for $REF, got '$DIGEST'"

SUBJECT="$NAME@$DIGEST"
IDENTITY="^https://github.com/BaryoDev/barakoCMS/.github/workflows/${WORKFLOW//./\\.}@"

echo "Verifying $SUBJECT"

cosign verify \
    --certificate-identity-regexp "$IDENTITY" \
    --certificate-oidc-issuer "$ISSUER" \
    "$SUBJECT" > /dev/null \
    || fail "the signature on $SUBJECT does not verify. Do not run this image."

# Provenance is attached to release images only. The :master image is signed but not attested.
if [ "$WORKFLOW" = "release.yml" ]; then
    cosign verify-attestation --type slsaprovenance1 \
        --certificate-identity-regexp "$IDENTITY" \
        --certificate-oidc-issuer "$ISSUER" \
        "$SUBJECT" > /dev/null \
        || fail "the provenance attestation on $SUBJECT does not verify. Do not run this image."
fi

echo "OK: $SUBJECT is signed by $WORKFLOW in BaryoDev/barakoCMS"
echo "Run it by digest to be sure you get exactly this image: $SUBJECT"

#!/usr/bin/env bash
# Verifies a published barakoCMS image before you run it.
#
#   bash scripts/verify-image-signature.sh ghcr.io/baryodev/barako-cms:6.0.0
#
# Resolves the reference to a digest, then checks three things against that digest: the cosign
# signature, the build provenance attestation and, for release images, the CycloneDX SBOM
# attestation. All must come from this repository's release workflow (GitHub OIDC, keyless).
# Release images are signed by release.yml. The :master image is signed by
# publish-master-image.yml, so verify it with:
#
#   VERIFY_WORKFLOW=publish-master-image.yml bash scripts/verify-image-signature.sh ghcr.io/baryodev/barako-cms:master
#
# Signed and attested: release :VERSION and :latest of barako-cms and barako-cms-decaf, and
# :master and :master-<sha> of barako-cms. Not signed: playground, dev, per-arch and Docker Hub
# mirror (arnelirobles/*) tags, and images published before signing was added. For a mirror image,
# verify the same digest against ghcr.io.
#
# Release images up to 5.10.0 have no SBOM attestation. For those, VERIFY_SBOM=0 skips that check
# and keeps the other two.
#
# Needs cosign 3 or later, docker (buildx) and jq on PATH. Exit 0 when all checks pass, 1 when one
# fails, 2 on bad usage. Only ghcr.io/baryodev/ images are accepted.
set -euo pipefail

REF="${1:-}"
WORKFLOW="${VERIFY_WORKFLOW:-release.yml}"
VERIFY_SBOM="${VERIFY_SBOM:-1}"
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

case "$VERIFY_SBOM" in
    0 | 1) ;;
    *) echo "error: VERIFY_SBOM must be 0 or 1" >&2; exit 2 ;;
esac

# Lowercase registry path, no whitespace, no shell syntax. The reference is only ever passed as a
# quoted argument, never through eval.
if ! [[ "$REF" =~ ^ghcr\.io/baryodev/[a-z0-9][a-z0-9._-]*((:[A-Za-z0-9._-]+)|(@sha256:[0-9a-f]{64}))?$ ]]; then
    echo "error: refusing '$REF', only ghcr.io/baryodev/<image>[:tag|@sha256:digest] is accepted" >&2
    exit 2
fi

COSIGN_INSTALL="https://docs.sigstore.dev/cosign/system_config/installation/"
command -v jq > /dev/null 2>&1 || fail "jq is not installed"
command -v cosign > /dev/null 2>&1 || fail "cosign is not installed, see $COSIGN_INSTALL"

# Images are signed with cosign 3 and its default bundle format, and the commands below use cosign 3's
# defaults, which cosign 2 does not share. A source build that reports no version is refused too.
COSIGN_VERSION=$(cosign version --json 2> /dev/null | jq -r '.gitVersion // empty' 2> /dev/null) || COSIGN_VERSION=""
COSIGN_MAJOR="${COSIGN_VERSION#v}"
COSIGN_MAJOR="${COSIGN_MAJOR%%.*}"
if ! [[ "$COSIGN_MAJOR" =~ ^[0-9]{1,6}$ ]]; then
    fail "could not read the cosign version, cosign 3 or later is needed, see $COSIGN_INSTALL"
fi
if [ "$((10#$COSIGN_MAJOR))" -lt 3 ]; then
    # Only plain version characters are printed, so cosign's output cannot put control codes on the terminal.
    COSIGN_SHOWN=$(printf '%s' "$COSIGN_VERSION" | LC_ALL=C tr -cd 'A-Za-z0-9.+_-' | cut -c1-40)
    fail "cosign $COSIGN_SHOWN is too old, cosign 3 or later is needed, see $COSIGN_INSTALL"
fi

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
IDENTITY="^https://github\\.com/BaryoDev/barakoCMS/\\.github/workflows/${WORKFLOW//./\\.}@refs/heads/master\$"

echo "Verifying $SUBJECT"

cosign verify \
    --certificate-identity-regexp "$IDENTITY" \
    --certificate-oidc-issuer "$ISSUER" \
    "$SUBJECT" > /dev/null \
    || fail "the signature on $SUBJECT does not verify. Do not run this image."

cosign verify-attestation --type slsaprovenance1 \
    --certificate-identity-regexp "$IDENTITY" \
    --certificate-oidc-issuer "$ISSUER" \
    "$SUBJECT" > /dev/null \
    || fail "the provenance attestation on $SUBJECT does not verify. Do not run this image."

# Only release images carry an SBOM attestation. The :master image does not.
if [ "$WORKFLOW" = "release.yml" ] && [ "$VERIFY_SBOM" = "1" ]; then
    cosign verify-attestation --type https://cyclonedx.org/bom \
        --certificate-identity-regexp "$IDENTITY" \
        --certificate-oidc-issuer "$ISSUER" \
        "$SUBJECT" > /dev/null \
        || fail "the SBOM attestation on $SUBJECT does not verify. Releases up to 5.10.0 have none: rerun with VERIFY_SBOM=0 for those. For a later release, do not run this image."
elif [ "$WORKFLOW" = "release.yml" ]; then
    echo "Skipped the SBOM attestation (VERIFY_SBOM=0)."
fi

echo "OK: $SUBJECT is signed by $WORKFLOW in BaryoDev/barakoCMS"
echo "Run it by digest to be sure you get exactly this image: $SUBJECT"

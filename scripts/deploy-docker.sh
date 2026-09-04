#!/bin/bash
set -euo pipefail

# Configuration
USERNAME="arnelirobles"
API_IMAGE="barako-cms"
TAG="${1:-latest}"

# Multi-arch build (amd64 for servers, arm64 for Apple Silicon) pushed straight
# to Docker Hub. The plain docker driver cannot do multi-platform builds, so use
# a docker-container builder (created once, reused afterwards).
#
# The API image only. The console image is built and published by BaryoDev/barakoBrew.
PLATFORMS="linux/amd64,linux/arm64"
BUILDER="barako-builder"

docker buildx inspect "$BUILDER" >/dev/null 2>&1 || docker buildx create --name "$BUILDER" --driver docker-container

echo "Building and pushing $USERNAME/$API_IMAGE:$TAG for ${PLATFORMS}..."
docker buildx build --builder "$BUILDER" --platform "$PLATFORMS" -t "$USERNAME/$API_IMAGE:$TAG" --push -f Dockerfile .

echo "Done."
echo "API: https://hub.docker.com/r/$USERNAME/$API_IMAGE"

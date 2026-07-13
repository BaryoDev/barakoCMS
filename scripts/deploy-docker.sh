#!/bin/bash
set -euo pipefail

# Configuration
USERNAME="arnelirobles"
API_IMAGE="barako-cms"
ADMIN_IMAGE="barako-admin"
TAG="${1:-latest}"

# Multi-arch build (amd64 for servers, arm64 for Apple Silicon) pushed straight
# to Docker Hub. The plain docker driver cannot do multi-platform builds, so use
# a docker-container builder (created once, reused afterwards).
PLATFORMS="linux/amd64,linux/arm64"
BUILDER="barako-builder"

docker buildx inspect "$BUILDER" >/dev/null 2>&1 || docker buildx create --name "$BUILDER" --driver docker-container

echo "🚀 Building and pushing $USERNAME/{$API_IMAGE,$ADMIN_IMAGE}:$TAG for ${PLATFORMS}..."

echo "📦 Backend API..."
docker buildx build --builder "$BUILDER" --platform "$PLATFORMS" -t "$USERNAME/$API_IMAGE:$TAG" --push -f Dockerfile .

echo "📦 Admin UI..."
docker buildx build --builder "$BUILDER" --platform "$PLATFORMS" -t "$USERNAME/$ADMIN_IMAGE:$TAG" --push -f admin/Dockerfile admin

echo "✅ Deployment complete!"
echo "API:   https://hub.docker.com/r/$USERNAME/$API_IMAGE"
echo "Admin: https://hub.docker.com/r/$USERNAME/$ADMIN_IMAGE"

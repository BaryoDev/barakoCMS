#!/bin/bash

# Configuration
USERNAME="arnelirobles"
API_IMAGE="barako-cms"
ADMIN_IMAGE="barako-admin"
TAG="latest"

echo "🚀 Starting Docker Hub Deployment for $USERNAME..."

# 1. Build and Tag Backend API
echo "📦 Building Backend API..."
docker build -t $USERNAME/$API_IMAGE:$TAG -f Dockerfile .

# 2. Build and Tag Admin UI
echo "📦 Building Admin UI..."
cd admin
docker build -t $USERNAME/$ADMIN_IMAGE:$TAG -f Dockerfile .
cd ..

# 3. Push to Docker Hub
echo "📤 Pushing images to Docker Hub..."
docker push $USERNAME/$API_IMAGE:$TAG
docker push $USERNAME/$ADMIN_IMAGE:$TAG

echo "✅ Deployment Complete!"
echo "API: $USERNAME/$API_IMAGE:$TAG"
echo "Admin: $USERNAME/$ADMIN_IMAGE:$TAG"

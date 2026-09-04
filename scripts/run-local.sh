#!/bin/bash
set -e

# BarakoCMS Local Run Script
# Usage: ./scripts/run-local.sh
#
# Runs docker-compose.prod.yml (published images, Caddy in front) on a loopback nip.io domain, so
# the production file gets exercised locally. The API only: the console is BaryoDev/barakoBrew and
# runs from its own repository, pointed at the URL printed at the end.

echo "========================================"
echo "BarakoCMS Local Deployment"
echo "========================================"

# 1. Configuration for loopback. nip.io resolves the name to 127.0.0.1, and Caddy issues an
# internal certificate for it, so the stack runs exactly as production does, on this machine.
DOMAIN_API="api.127.0.0.1.nip.io"
ACME_EMAIL="local@test.com"

# 2. Generate secrets (if .env doesn't exist)
if [ ! -f .env ]; then
    echo "Creating .env file..."
    DB_PASSWORD=$(openssl rand -base64 24 | tr -dc 'a-zA-Z0-9')
    JWT_KEY=$(openssl rand -base64 48 | tr -dc 'a-zA-Z0-9')
    ADMIN_PASSWORD="Barako-Local-123!"

    cat <<ENV > .env
# Domain (loopback)
DOMAIN_API=$DOMAIN_API
ACME_EMAIL=$ACME_EMAIL

# Origins allowed to call the API from a browser. http, not https: a console served over plain
# http sends "http://localhost:3000" as its Origin, so an https entry here matches nothing and
# every call fails CORS.
FRONTEND_ORIGINS=http://localhost:3000

# Which published image to run. latest is right here; production pins a version.
BARAKO_TAG=latest

# Database
DB_NAME=barako_cms
DB_USER=barako_user
DB_PASSWORD=$DB_PASSWORD

# Security
JWT_KEY=$JWT_KEY

# Initial Admin Seeding
ADMIN_USER=admin
ADMIN_PASSWORD=$ADMIN_PASSWORD
ENV
else
    echo "Using existing .env file."
fi

# 3. Publish Caddy on the standard ports and pin the domain, without editing the production file.
cat <<OVERRIDE > docker-compose.override.yml
services:
  caddy:
    ports:
      - "80:80"
      - "443:443"
    environment:
      - DOMAIN_API=$DOMAIN_API
OVERRIDE

# Keep .env in step with the override.
sed -i 's/DOMAIN_API=.*/DOMAIN_API=api.127.0.0.1.nip.io/' .env

echo "Configured for a nip.io loopback domain."
echo "- API: https://$DOMAIN_API"

# No --build: docker-compose.prod.yml runs published images, so there is nothing to compile.
docker compose -f docker-compose.prod.yml -f docker-compose.override.yml up -d

echo ""
echo "Stack started. Allow a moment for the images to pull."
echo "API:     https://$DOMAIN_API (accept the self-signed certificate)"
echo "Health:  https://$DOMAIN_API/health"
echo "Console: run BaryoDev/barakoBrew and point it at https://$DOMAIN_API"
rm docker-compose.override.yml

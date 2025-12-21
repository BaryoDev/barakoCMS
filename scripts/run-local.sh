#!/bin/bash
set -e

# BarakoCMS Local Run Script
# Usage: ./scripts/run-local.sh

echo "========================================"
echo "🏠 BarakoCMS Local Deployment"
echo "========================================"

# 1. Configuration for Localhost
DOMAIN_API="localhost:8080"
DOMAIN_ADMIN="localhost:3000"
ACME_EMAIL="local@test.com" # Caddy uses internal issuer for localhost automatically

echo "Configuring for Localhost..."

# 2. Generate Secrets (if .env doesn't exist)
if [ ! -f .env ]; then
    echo "Creating .env file..."
    DB_PASSWORD=$(openssl rand -base64 24 | tr -dc 'a-zA-Z0-9')
    JWT_KEY=$(openssl rand -base64 48 | tr -dc 'a-zA-Z0-9')
    ADMIN_PASSWORD="Barako-Local-123!"

    cat <<EOF > .env
# Domains (Localhost)
DOMAIN_API=$DOMAIN_API
DOMAIN_ADMIN=$DOMAIN_ADMIN
ACME_EMAIL=$ACME_EMAIL

# Database
DB_NAME=barako_cms
DB_USER=barako_user
DB_PASSWORD=$DB_PASSWORD

# Security
JWT_KEY=$JWT_KEY

# Initial Admin Seeding
ADMIN_USER=admin
ADMIN_PASSWORD=$ADMIN_PASSWORD
EOF
else
    echo "Using existing .env file."
fi

# 3. Create Caddyfile.local if needed
# Note: The production Caddyfile uses {$DOMAIN_API} variables which work fine,
# but for localhost we want to avoid auto-https issues if ports are weird.
# Actually, the prod Caddyfile blocks are:
# {$DOMAIN_API} { reverse_proxy app:8080 }
# If DOMAIN_API is "localhost:8080", Caddy will serve on port 8080.
# We need to ensure docker-compose maps these ports correctly.
# In prod compose: 80:80, 443:443.
# We might need a separate docker-compose.local.yml to map ports differently if 80/443 are busy.
# For simplicity, let's assume we want to access via http://localhost:3000 (Admin) and http://localhost:8080 (API) directly?
# OR we use Caddy to map localhost:80 -> Admin?
# The PROD compose maps 80->80.
# If we set DOMAIN_ADMIN=localhost, Caddy allows "http://localhost".
# Let's try to map:
# Admin -> http://localhost (via Caddy 80)
# API -> http://api.localhost (via Caddy 80) -- requires /etc/hosts modification.
# EASIER:
# Admin -> http://localhost:9000
# API -> http://localhost:9001
# But Caddy inside container listens on 80.
# Let's use the PROD file but rely on Caddy's internal handling.

# DECISION: To purely simulate production, we should use domains.
# But for "Local Run", users typically access via ports.
# The `docker-compose.prod.yml` exposes Caddy 80:80 / 443:443.
# Configuring Caddy with "localhost" makes it use internal certs on https://localhost.

echo ""
echo "🚀 Starting Stack..."
echo "Admin will be verified at: https://localhost (Accept self-signed cert)"
echo "API will be accessible at: https://localhost/api... wait, we need routing."

# RE-STRATEGY for Local:
# We generally want to bypass Caddy locally OR use Caddy with port mapping.
# If we use Caddy to route hostnames, we need domains.
# If we use localhost, we can't distinguish API vs Admin on the same port 443 easily without path routing.
# BarakoCMS Admin expects API on a separate domain usually, OR we need path routing /api.

# SIMPLIFIED LOCAL STRATEGY:
# Let's just run them on separate ports and bypass Caddy for simplicity?
# NO, we want to test the "Prod" stack.
# Let's use Caddyfile modification to use Path Routing for Local?
# Too complex.

# BEST LOCAL STRATEGY:
# Map custom ports for Caddy blocks.
# API -> :8443
# Admin -> :8444
# But we need to update Caddyfile to accept env var ports?
# Creating a temporary override compose file is best.

cat <<EOF > docker-compose.override.yml
services:
  caddy:
    ports:
      - "80:80"
      - "443:443"
    environment:
      - DOMAIN_ADMIN=localhost
      # API on a different port? Caddy can't multiplex domains on localhost easily regarding cookies/cors usually.
      # Let's try 'localhost' for Admin and '127.0.0.1' for API? hacky.
      # Let's use nip.io!
      - DOMAIN_ADMIN=admin.127.0.0.1.nip.io
      - DOMAIN_API=api.127.0.0.1.nip.io
EOF

# Update .env to match nip.io
sed -i 's/DOMAIN_API=.*/DOMAIN_API=api.127.0.0.1.nip.io/' .env
sed -i 's/DOMAIN_ADMIN=.*/DOMAIN_ADMIN=admin.127.0.0.1.nip.io/' .env

echo "Configured for nip.io domains (Loopback)."
echo "- Admin: https://admin.127.0.0.1.nip.io"
echo "- API:   https://api.127.0.0.1.nip.io"

docker compose -f docker-compose.prod.yml -f docker-compose.override.yml up -d --build

echo ""
echo "✅ Stack Started!"
echo "Please allow a few minutes for the build."
echo "Access Admin UI: https://admin.127.0.0.1.nip.io (Accept Self-Signed Cert)"
rm docker-compose.override.yml

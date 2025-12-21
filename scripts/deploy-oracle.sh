#!/bin/bash
set -e

# BarakoCMS Automated Deployment Script
# Usage: ./deploy-oracle.sh

echo "========================================"
echo "☕  BarakoCMS Deployment Utility"
echo "========================================"

# 1. Check for Docker
if ! command -v docker &> /dev/null; then
    echo "❌ Docker is not installed."
    echo "Please install Docker first: curl -fsSL https://get.docker.com | sh"
    exit 1
fi

# 2. User Inputs
echo ""
echo "Please provide configuration details:"
read -p "Enter your base domain (e.g., barakocms.com): " DOMAIN_BASE
read -p "Enter your email (for SSL certificates): " ACME_EMAIL

DOMAIN_API="api.$DOMAIN_BASE"
DOMAIN_ADMIN="admin.$DOMAIN_BASE"

echo ""
echo "Creating Environment..."

# 3. Generate Secrets
DB_PASSWORD=$(openssl rand -base64 24 | tr -dc 'a-zA-Z0-9')
JWT_KEY=$(openssl rand -base64 48 | tr -dc 'a-zA-Z0-9')
ADMIN_PASSWORD="Barako-$(openssl rand -hex 4)!"

# 4. Create .env
cat <<EOF > .env
# Domains
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

echo "✅ .env file created."

# 5. Deploy
echo ""
echo "🚀 Building and Deploying containers..."
echo "This may take a few minutes (building Admin UI from source)..."

docker compose -f docker-compose.prod.yml up -d --build

# 6. Summary
echo ""
echo "========================================"
echo "✅  Deployment Complete!"
echo "========================================"
echo "API URL:        https://$DOMAIN_API"
echo "Admin Dashboard: https://$DOMAIN_ADMIN"
echo ""
echo "--- Credentials ---"
echo "DB User:        barako_user"
echo "DB Password:    $DB_PASSWORD"
echo ""
echo "Admin Login:    admin"
echo "Admin Password: $ADMIN_PASSWORD"
echo "========================================"
echo "⚠️  SAVE THESE CREDENTIALS NOW! They are stored in .env"

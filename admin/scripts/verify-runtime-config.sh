#!/bin/bash

# This script simulates the Docker entrypoint behavior to verify
# that environment variables are correctly translated into public/env-config.js

# 1. Setup
ADMIN_DIR="/Users/arnelirobles/barakoCMS/admin"
TEMP_ENV_CONFIG="$ADMIN_DIR/public/env-config.js.bak"

# Backup existing config if it exists
if [ -f "$ADMIN_DIR/public/env-config.js" ]; then
    mv "$ADMIN_DIR/public/env-config.js" "$TEMP_ENV_CONFIG"
fi

# 2. Mock environment variables
export NEXT_PUBLIC_API_URL="https://mock-api.barakocms.com"
export NEXT_PUBLIC_ANOTHER_VAR="test-value"

echo "Step 1: Running entrypoint.sh simulation..."
# We need to set /app directory relative to where we run it
# Since the script uses /app/public, let's mock the structure or just run it with a patch
# For testing purposes, let's create a temporary app dir
mkdir -p /tmp/barako-test/public
cp "$ADMIN_DIR/entrypoint.sh" /tmp/barako-test/
chmod +x /tmp/barako-test/entrypoint.sh

# Run from /tmp
cd /tmp/barako-test
./entrypoint.sh echo "Success"

echo "Step 2: Verifying generated file..."
cat /tmp/barako-test/public/env-config.js

# Check for expected content
if grep -q "https://mock-api.barakocms.com" /tmp/barako-test/public/env-config.js; then
    echo "✅ Verification SUCCESS: API URL detected in generated config."
else
    echo "❌ Verification FAILED: API URL not found."
    exit 1
fi

# 3. Cleanup
rm -rf /tmp/barako-test
if [ -f "$TEMP_ENV_CONFIG" ]; then
    mv "$TEMP_ENV_CONFIG" "$ADMIN_DIR/public/env-config.js"
fi

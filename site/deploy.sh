#!/usr/bin/env bash
# Build the site and deploy the static export to the VM that serves barakocms.baryo.dev.
#
# The marketplace is rendered at build time from NuGet, so re-run this after a release to pick up
# new packages and current download counts.
set -euo pipefail

VM="${VM:-opc@140.245.103.105}"
KEY="${KEY:-$HOME/.ssh/oracle_vm_key}"
DEST="/var/www/barakocms-site"

echo "→ building"
# Next's fetch cache persists in .next between runs, so without clearing it a rebuild can replay a
# stale NuGet response and show yesterday's module list as if it were current.
rm -rf .next out
npm run build

echo "→ syncing out/ to $VM:$DEST"
# $DEST is root-owned, so the remote rsync needs sudo to write and delete there.
rsync -az --delete --rsync-path="sudo rsync" -e "ssh -i $KEY" out/ "$VM:$DEST/"

echo "→ restoring SELinux context and reloading nginx"
ssh -i "$KEY" "$VM" 'sudo restorecon -R /var/www/barakocms-site 2>/dev/null || true; sudo nginx -t && sudo systemctl reload nginx'

echo "✓ https://barakocms.baryo.dev"

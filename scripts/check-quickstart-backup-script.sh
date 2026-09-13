#!/usr/bin/env bash
# The quickstart ships its own copy of scripts/backup-cron.sh, because an operator copies the
# quickstart folder out of the checkout and a mount of ../scripts then points at nothing (#712).
#
# A copy is a second place for the logic that stops a failed dump reporting success, and the next
# fix would reach only one of them. This fails when they differ, so they cannot drift apart.

set -uo pipefail

cd "$(dirname "$0")/.."

source_script="scripts/backup-cron.sh"
copy_script="quickstart/scripts/backup-cron.sh"

for f in "$source_script" "$copy_script"; do
  [ -f "$f" ] || { echo "check-quickstart-backup-script: $f is missing"; exit 1; }
done

if cmp -s "$source_script" "$copy_script"; then
  echo "$copy_script matches $source_script."
  exit 0
fi

echo "check-quickstart-backup-script: $copy_script differs from $source_script."
diff -u "$source_script" "$copy_script"
echo
echo "Change both together. To take the repository's version: cp $source_script $copy_script"
exit 1

#!/usr/bin/env bash
# The quickstart ships its own copies of scripts/backup-cron.sh and scripts/db-app-role.sql, because
# an operator copies the quickstart folder out of the checkout and a mount of ../scripts then points
# at nothing (#712).
#
# A copy is a second place for the logic that stops a failed dump reporting success or that keeps
# the API's database role without superuser, and the next fix would reach only one of them. This
# fails when a pair differs, so they cannot drift apart.

set -uo pipefail

cd "$(dirname "$0")/.."

pairs=(
  "scripts/backup-cron.sh quickstart/scripts/backup-cron.sh"
  "scripts/db-app-role.sql quickstart/scripts/db-app-role.sql"
)

status=0
for pair in "${pairs[@]}"; do
  read -r source_script copy_script <<<"$pair"

  missing=0
  for f in "$source_script" "$copy_script"; do
    if [ ! -f "$f" ]; then
      echo "check-quickstart-backup-script: $f is missing"
      missing=1
    fi
  done
  if [ "$missing" -ne 0 ]; then
    status=1
    continue
  fi

  if cmp -s "$source_script" "$copy_script"; then
    echo "$copy_script matches $source_script."
    continue
  fi

  echo "check-quickstart-backup-script: $copy_script differs from $source_script."
  diff -u "$source_script" "$copy_script"
  echo
  echo "Change both together. To take the repository's version: cp $source_script $copy_script"
  status=1
done

exit "$status"

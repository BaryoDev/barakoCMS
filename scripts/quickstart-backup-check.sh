#!/usr/bin/env bash
#
# Proves the quickstart folder takes a backup once it has been copied out of the repository.
#
# The quickstart README says to copy the folder and run it. Its backup service used to mount
# ../scripts, which only exists inside a checkout, so a copied folder crash-looped the backup
# container while every other service came up fine (#712). "The stack started" did not catch that,
# so this waits for the backup's own success line instead.
#
# The sequence:
#
#   1. copy quickstart/ to two temporary directories outside the checkout
#   2. bring up postgres and db-backup in both, as separate compose projects on one host
#   3. create the table the backup waits for, with enough data to clear its minimum size
#   4. each backup container logs "Backup OK" and leaves an archive in its volume
#
# Two copies, because the fixed container_name the service used to carry made a second copy fail
# to start. The API is not started: the backup's precondition is a schema in Postgres, and standing
# up the whole application is what the restore check already does.
#
# Usage: scripts/quickstart-backup-check.sh

set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$PWD"
WORK="$(mktemp -d)"
PROJECTS=("qs-backup-check-a-$$" "qs-backup-check-b-$$")

cleanup() {
    for p in "${PROJECTS[@]}"; do
        [ -d "$WORK/$p" ] || continue
        (cd "$WORK/$p" && docker compose -p "$p" down -v --remove-orphans >/dev/null 2>&1) || true
    done
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

for p in "${PROJECTS[@]}"; do
    step "copying quickstart/ to $WORK/$p"
    cp -R "$REPO/quickstart" "$WORK/$p"
    cat > "$WORK/$p/.env" <<'ENV'
DB_PASSWORD=quickstart-check-not-a-real-password
JWT_KEY=quickstart-check-not-a-real-key-at-least-32-characters
ADMIN_PASSWORD=quickstart-check-not-a-real-password
ENV
    (cd "$WORK/$p" && docker compose -p "$p" config >/dev/null) || fail "the copied compose file does not resolve"
    (cd "$WORK/$p" && docker compose -p "$p" up -d postgres db-backup) || fail "compose up failed for $p"
done

for p in "${PROJECTS[@]}"; do
    step "creating the schema the backup waits for in $p"
    for _ in $(seq 1 30); do
        (cd "$WORK/$p" && docker compose -p "$p" exec -T postgres pg_isready -U postgres >/dev/null 2>&1) && break
        sleep 2
    done
    (cd "$WORK/$p" && docker compose -p "$p" exec -T postgres psql -U postgres -d barakocms -v ON_ERROR_STOP=1 -q -c "
        create table if not exists public.mt_doc_users (id uuid primary key, data jsonb not null);
        insert into public.mt_doc_users
            select gen_random_uuid(), jsonb_build_object('n', g, 'pad', repeat('x', 64))
            from generate_series(1, 200) g;") || fail "could not create the schema in $p"
done

for p in "${PROJECTS[@]}"; do
    step "waiting for the first backup in $p"
    ok=0
    for _ in $(seq 1 60); do
        logs=$(cd "$WORK/$p" && docker compose -p "$p" logs --no-color db-backup 2>&1 || true)
        if printf '%s' "$logs" | grep -q "Backup OK"; then ok=1; break; fi
        if printf '%s' "$logs" | grep -qE "BACKUP FAILED|No such file or directory"; then break; fi
        sleep 2
    done
    printf '%s\n' "$logs" | tail -20
    [ "$ok" = 1 ] || fail "the backup container in $p did not log a successful backup"

    state=$(cd "$WORK/$p" && docker compose -p "$p" ps --format '{{.Name}} {{.State}}' db-backup)
    echo "$state"
    case "$state" in *running*) ;; *) fail "the backup container in $p is not running: $state" ;; esac

    archive=$(cd "$WORK/$p" && docker compose -p "$p" exec -T db-backup sh -c 'ls -1 /backups/barako_backup_*.sql.gz | head -1')
    [ -n "$archive" ] || fail "no archive in the backups volume of $p"
    echo "archive: $archive"
done

printf '\nTwo copies of quickstart/, outside the repository, each took a backup on one host.\n'

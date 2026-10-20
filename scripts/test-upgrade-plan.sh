#!/usr/bin/env bash
# Tests the upgrade plan scripts/lib-migrations.sh reads from migrations/, which upgrade-check.sh
# applies and prints with --list.
#
# The plan used to be written out by hand in upgrade-check.sh and twice in docs/upgrading-to-4.0.md,
# and every open migration pull request conflicted with every other on those lines. These cases pin
# the order CI proved for the files already shipped, and show that a new file joins the plan with no
# edit anywhere else.
#
# Nothing here starts a container, builds anything or touches the network.
#
#   bash scripts/test-upgrade-plan.sh

set -uo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
LIB="$ROOT/scripts/lib-migrations.sh"

pass=0
fail=0
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

out=""
code=0
plan() { # $1 = directory standing in for the repository root, $2 = FROM_VERSION
    out=$(cd "$1" && set -e && . "$LIB" && migration_plan "$2" 2>&1)
    code=$?
}
psql_lines() { # same arguments
    out=$(cd "$1" && set -e && . "$LIB" && migration_plan_psql "$2" 2>&1)
    code=$?
}

ok() { echo "  ok    $1"; pass=$((pass + 1)); }
bad() {
    echo "  FAIL  $1"
    printf '%s\n' "$out" | sed 's/^/          /'
    fail=$((fail + 1))
}
expect() { # $1 = description, $2 = expected text, $3 = actual text
    if [ "$2" = "$3" ]; then
        ok "$1"
    else
        echo "  FAIL  $1"
        diff <(printf '%s\n' "$2") <(printf '%s\n' "$3") | sed 's/^/          /'
        fail=$((fail + 1))
    fi
}

# A copy holding only what the plan reads: migrations/ and the module project files.
copy_repo() { # $1 = destination
    mkdir -p "$1"
    cp -R "$ROOT/migrations" "$1/migrations"
    local csproj
    for csproj in "$ROOT"/BarakoCMS.*/*.csproj; do
        mkdir -p "$1/$(basename "$(dirname "$csproj")")"
        cp "$csproj" "$1/$(basename "$(dirname "$csproj")")/"
    done
}

# The order upgrade-check.sh applied by hand through 4.7.0, before the plan was read from the
# directory. A file added later is not here and the comparison below ignores it, so this list never
# needs an edit for a new migration.
proven=$(cat <<'EOF'
core migrations/4.0.0/3.x-to-4.0.sql
core migrations/4.2.0/user-normalized-identity.sql
core migrations/4.2.0/site-share-links.sql
core migrations/4.3.0/collection-syncs.sql
core migrations/4.3.0/marten-9-37-event-store-columns.sql
core migrations/4.4.0/marten-9-38-quick-append-events.sql
core migrations/4.5.0/refresh-token-hash-index.sql
core migrations/4.6.0/event-correlation-metadata.sql
core migrations/4.6.0/sensitivity-by-capability.sql
core migrations/4.6.0/tenant-profile-to-site.sql
core migrations/4.7.0/membership-unique-user-tenant.sql
core migrations/4.7.0/tenant-policy-restore.sql
module migrations/4.2.0/stored-files-parent-index.sql
module migrations/4.2.0/forms-public-forms.sql
module migrations/4.5.0/email-sent-emails.sql
module migrations/4.6.0/external-auth-identities.sql
module migrations/4.6.0/forms-email-verification.sql
rollback migrations/4.7.0/rollback-tenant-policy-restore.sql
rollback migrations/4.7.0/rollback-membership-unique-user-tenant.sql
rollback migrations/4.6.0/rollback-forms-email-verification.sql
rollback migrations/4.6.0/rollback-external-auth-identities.sql
rollback migrations/4.6.0/rollback-sensitivity-by-capability.sql
rollback migrations/4.6.0/rollback-tenant-profile-to-site.sql
rollback migrations/4.6.0/rollback-event-correlation-metadata.sql
rollback migrations/4.5.0/rollback-email-sent-emails.sql
rollback migrations/4.5.0/rollback-refresh-token-hash-index.sql
rollback migrations/4.4.0/rollback-marten-9-38-quick-append-events.sql
rollback migrations/4.3.0/rollback-collection-syncs.sql
rollback migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql
rollback migrations/4.2.0/rollback-site-share-links.sql
rollback migrations/4.2.0/rollback-user-normalized-identity.sql
rollback migrations/4.0.0/rollback-to-3.x.sql
EOF
)

reverse_lines() { awk '{ a[NR] = $0 } END { for (i = NR; i > 0; i--) print a[i] }'; }

only_proven() { grep -xF -f <(printf '%s\n' "$proven") <<< "$1" || true; }

echo "== the files already shipped =="
plan "$ROOT" 3.21.0
if [ "$code" = 0 ]; then ok "the plan from 3.21.0 builds"; else bad "the plan from 3.21.0 builds"; fi
expect "from 3.21.0 they run in the order CI proved by hand" "$proven" "$(only_proven "$out")"

listed=$(awk '{ print $2 }' <<< "$out" | sort)
on_disk=$(cd "$ROOT" && find migrations -name '*.sql' -not -path 'migrations/tenancy/*' | sort)
[ -n "$on_disk" ] || bad "migrations/ holds no files, so the next check has nothing to compare"
expect "every file under a version folder is listed exactly once" "$on_disk" "$listed"

plan "$ROOT" 4.1.0
expect "from 4.1.0 the 4.0.0 folder is left out and the rest keep their order" \
    "$(grep -v 'migrations/4\.0\.0/' <<< "$proven")" "$(only_proven "$out")"

plan "$ROOT" 4.6.0
if [ "$code" = 0 ] && grep -q ' migrations/4\.7\.0/' <<< "$out" && ! grep -q ' migrations/4\.[0-6]\.' <<< "$out"; then
    ok "from 4.6.0 only newer folders are listed"
else
    bad "from 4.6.0 only newer folders are listed"
fi

# The 5.0 files CI has proved from 4.9.0, the start the upgrade job runs besides 4.1.0. A later 5.0
# file joins the plan beside these, and the comparison below ignores it, as above.
proven_50=$(cat <<'EOF'
core migrations/5.0.0/marten-9-49-quick-append-events.sql
rollback migrations/5.0.0/rollback-marten-9-49-quick-append-events.sql
EOF
)

plan "$ROOT" 4.9.0
if [ "$code" = 0 ]; then ok "the plan from 4.9.0 builds"; else bad "the plan from 4.9.0 builds"; fi
expect "from 4.9.0 the 5.0.0 files run in the order CI proved" \
    "$proven_50" "$(grep -xF -f <(printf '%s\n' "$proven_50") <<< "$out" || true)"
if grep -q ' migrations/4\.' <<< "$out"; then
    bad "from 4.9.0 no 4.x folder is listed"
else
    ok "from 4.9.0 no 4.x folder is listed"
fi

# From 4.1.0 every 5.0.0 core file runs after every older core file, and the core rollbacks undo
# those same files in reverse. Folders newer than 5.0.0 (5.1.0 and on) run after the 5.0.0 files and
# are undone before them, so they are taken out of both lists first and checked on their own below.
# A module's own 5.0.0 file is undone before any core file, so module rollbacks are skipped when
# comparing, whatever the module.
module_files=$(cd "$ROOT" && . "$LIB" && module_migration_paths)
plan "$ROOT" 4.1.0
newer_than_50() { # reads paths on stdin, keeps those in a folder after 5.0.0
    while read -r path; do
        folder=${path#migrations/}; folder=${folder%%/*}
        [ "$(printf '%s\n%s\n' 5.0.0 "$folder" | sort -V | tail -n 1)" != 5.0.0 ] && echo "$path"
    done
    return 0
}
core_all=$(grep '^core ' <<< "$out" | sed 's/^core //')
core_later=$(newer_than_50 <<< "$core_all")
core_50=$(grep '^migrations/5\.0\.0/' <<< "$core_all" || true)
count_50=$(grep -c . <<< "$core_50" || true)
count_later=$(grep -c . <<< "$core_later" || true)
core_rollbacks_all=$(grep '^rollback ' <<< "$out" | while read -r _ path; do
    grep -qxF "${path%/*}/${path##*/rollback-}" <<< "$module_files" || echo "$path"
done)
expected_rollbacks=$(reverse_lines <<< "$core_50" | sed 's#/\([^/]*\)$#/rollback-\1#')
core_without_later=$(grep -vxF -f <(printf '%s\n' "$core_later" "") <<< "$core_all" || true)
rollbacks_50=$(tail -n +"$((count_later + 1))" <<< "$core_rollbacks_all" | head -n "$count_50")
if [ "$count_50" -gt 0 ] \
    && grep -qxF migrations/5.0.0/marten-9-49-quick-append-events.sql <<< "$core_50" \
    && [ "$(tail -n "$count_50" <<< "$core_without_later")" = "$core_50" ] \
    && [ "$rollbacks_50" = "$expected_rollbacks" ]; then
    ok "from 4.1.0 the 5.0.0 core files run last among the older core files and their rollbacks come first, in reverse"
else
    bad "from 4.1.0 the 5.0.0 core files run last among the older core files and their rollbacks come first, in reverse"
fi

# Folders after 5.0.0 run after every 5.0.0 core file, in version order, and their rollbacks come
# before any 5.0.0 rollback, in reverse.
expected_later_rollbacks=$(reverse_lines <<< "$core_later" | sed 's#/\([^/]*\)$#/rollback-\1#' | while read -r path; do
    grep -qxF "$path" <<< "$core_rollbacks_all" && echo "$path"
done)
if [ "$count_later" -gt 0 ] \
    && grep -qxF migrations/5.1.0/workflow-run-updated-at-index.sql <<< "$core_later" \
    && [ "$(tail -n "$count_later" <<< "$core_all")" = "$core_later" ] \
    && [ "$(sort -V -s -t/ -k2,2 <<< "$core_later")" = "$core_later" ] \
    && [ "$(head -n "$(grep -c . <<< "$expected_later_rollbacks")" <<< "$core_rollbacks_all")" = "$expected_later_rollbacks" ]; then
    ok "from 4.1.0 the 5.1.0 core files run after the 5.0.0 ones and their rollbacks come before them"
else
    bad "from 4.1.0 the 5.1.0 core files run after the 5.0.0 ones and their rollbacks come before them"
fi

# From 5.0.0 on, the 5.0.0 folder is left out and 5.1.0 is all that is listed.
plan "$ROOT" 5.0.0
if [ "$code" = 0 ] && grep -q ' migrations/5\.1\.0/' <<< "$out" && ! grep -q ' migrations/[0-4]\.\|migrations/5\.0\.' <<< "$out"; then
    ok "from 5.0.0 only the 5.1.0 and newer folders are listed"
else
    bad "from 5.0.0 only the 5.1.0 and newer folders are listed"
fi

psql_lines "$ROOT" 3.21.0
if grep -qxF 'psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f migrations/4.2.0/stored-files-parent-index.sql' <<< "$out" \
    && grep -qxF 'psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/forms-public-forms.sql' <<< "$out"; then
    ok "a no-transaction file is printed without --single-transaction, and only that one"
else
    bad "a no-transaction file is printed without --single-transaction, and only that one"
fi

echo "== a new migration =="
repo="$work/new-core"
copy_repo "$repo"
mkdir -p "$repo/migrations/9.9.0"
printf -- '-- a core file\nselect 1;\n' > "$repo/migrations/9.9.0/b-change.sql"
printf -- '-- its rollback\nselect 1;\n' > "$repo/migrations/9.9.0/rollback-b-change.sql"
printf -- '-- another, no rollback\n-- barako:no-transaction\nselect 1;\n' > "$repo/migrations/9.9.0/a-change.sql"
plan "$repo" 3.21.0
expect "the shipped files keep their order beside it" "$proven" "$(only_proven "$out")"
expect "new core files run last among core, in name order" \
    "core migrations/9.9.0/a-change.sql
core migrations/9.9.0/b-change.sql" "$(grep '^core ' <<< "$out" | tail -2)"
expect "its rollback runs first" "rollback migrations/9.9.0/rollback-b-change.sql" "$(grep '^rollback ' <<< "$out" | head -1)"
psql_lines "$repo" 3.21.0
if grep -qxF 'psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f migrations/9.9.0/a-change.sql' <<< "$out"; then
    ok "its no-transaction marker is read from the file"
else
    bad "its no-transaction marker is read from the file"
fi

repo="$work/new-module"
copy_repo "$repo"
mkdir -p "$repo/migrations/9.9.0"
printf -- 'select 1;\n' > "$repo/migrations/9.9.0/forms-thing.sql"
forms=$(ls "$repo"/BarakoCMS.Forms/*.csproj)
perl -pi -e 's#</Project>#  <ItemGroup><EmbeddedResource Include="..\\migrations\\9.9.0\\forms-thing.sql" /></ItemGroup>\n</Project>#' "$forms"
plan "$repo" 3.21.0
expect "a file a module embeds runs last among module files" "module migrations/9.9.0/forms-thing.sql" "$(grep '^module ' <<< "$out" | tail -1)"

repo="$work/pinned"
copy_repo "$repo"
printf -- 'select 1;\n' > "$repo/migrations/4.6.0/late-addition.sql"
plan "$repo" 3.21.0
if [ "$code" != 0 ] && grep -qF "on disk only: late-addition.sql" <<< "$out"; then
    ok "a file added to a pinned shipped folder stops the plan and is named"
else
    bad "a file added to a pinned shipped folder stops the plan and is named"
fi

echo "== nothing else lists the files by hand =="
hand=$(grep -nE '^[^#]*docker cp migrations/[0-9]' "$ROOT/scripts/upgrade-check.sh" | grep -v 'event-correlation-early' || true)
out=$hand
if [ -z "$hand" ]; then ok "upgrade-check.sh copies no named file but the early 4.6.0 one"; else bad "upgrade-check.sh copies no named file but the early 4.6.0 one"; fi
for guide in docs/upgrading-to-4.0.md docs/upgrading-to-5.0.md; do
    out=$(grep -nE -- '-f migrations/[0-9]' "$ROOT/$guide" 2>&1 || true)
    if [ -f "$ROOT/$guide" ] && [ -z "$out" ]; then ok "$guide has no hand list of psql lines"; else bad "$guide has no hand list of psql lines"; fi
done

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]

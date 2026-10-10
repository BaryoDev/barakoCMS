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
sed -i 's#</Project>#  <ItemGroup><EmbeddedResource Include="..\\migrations\\9.9.0\\forms-thing.sql" /></ItemGroup>\n</Project>#' "$forms"
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
out=$(grep -nE -- '-f migrations/[0-9]' "$ROOT/docs/upgrading-to-4.0.md" || true)
if [ -z "$out" ]; then ok "docs/upgrading-to-4.0.md has no hand list of psql lines"; else bad "docs/upgrading-to-4.0.md has no hand list of psql lines"; fi

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]

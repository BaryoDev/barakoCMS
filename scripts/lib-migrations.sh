# shellcheck shell=bash
#
# The upgrade plan, read from the files under migrations/ rather than from a list kept by hand.
# Sourced by upgrade-check.sh, which applies it and prints it (--list), and by test-upgrade-plan.sh.
# Run from the repository root: every path here is relative to it.
#
# The rules, which a new migration needs no edit anywhere to follow:
#
#   - a folder named for a version runs when that version is newer than the one the database
#     started on, oldest folder first. migrations/tenancy is not a version and is never listed.
#   - a file a module's .csproj embeds is a module file. Every other file is core's. All core files
#     run first, then all module files, because core's db-assert is checked between the two.
#   - within a folder, files run in name order.
#   - the rollback runs newest folder first, and within a folder in the reverse of the order the
#     forward files ran, rollback-<name>.sql undoing <name>.sql. A rollback file with no forward
#     file of that name, such as 4.0.0/rollback-to-3.x.sql, runs last in its folder.
#   - a file whose header carries "-- barako:no-transaction" is applied without
#     --single-transaction.
#
# Three shipped folders were ordered by hand before these rules existed, and their order is the one
# CI has proven on every upgrade since, so it is pinned below instead of being changed. A pinned
# folder must list every .sql file it holds, and the plan refuses to build when it does not. New
# folders are not pinned.

MIGRATIONS_DIR=migrations

migration_pinned_order() { # $1 = version folder; prints its files in run order, or nothing
    case "$1" in
        4.2.0) printf '%s\n' \
            user-normalized-identity.sql site-share-links.sql \
            stored-files-parent-index.sql forms-public-forms.sql \
            rollback-site-share-links.sql rollback-user-normalized-identity.sql ;;
        4.3.0) printf '%s\n' \
            collection-syncs.sql marten-9-37-event-store-columns.sql \
            rollback-collection-syncs.sql rollback-marten-9-37-event-store-columns.sql ;;
        4.6.0) printf '%s\n' \
            event-correlation-metadata.sql sensitivity-by-capability.sql tenant-profile-to-site.sql \
            external-auth-identities.sql forms-email-verification.sql \
            rollback-forms-email-verification.sql rollback-external-auth-identities.sql \
            rollback-sensitivity-by-capability.sql rollback-tenant-profile-to-site.sql \
            rollback-event-correlation-metadata.sql ;;
    esac
}

version_newer() { # true when $1 is a newer version than $2
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}

module_migration_paths() { # the migrations/ paths any module's .csproj embeds, one per line
    local csproj
    for csproj in BarakoCMS.*/*.csproj; do
        [ -f "$csproj" ] || continue
        case "$csproj" in BarakoCMS.Tests/*) continue ;; esac
        sed -n 's/.*Include="\.\.[\\/]\(migrations[\\/][^"*]*\.sql\)".*/\1/p' "$csproj" | tr '\\' '/'
    done | sort -u
}

migration_is_transactional() { # $1 = path; false when the file says it cannot run in a transaction
    ! grep -q '^-- barako:no-transaction[[:space:]]*$' "$1"
}

# Prints the plan as "core <path>", "module <path>" and "rollback <path>" lines, in run order.
# $1 = the version the database started on. Returns 1, after saying why on stderr, when a pinned
# folder and the files on disk disagree.
migration_plan() {
    local from=$1 modules dir version pinned file name core_up=() module_up=() down=() forward
    modules=$(module_migration_paths)

    local versions
    versions=$(for dir in "$MIGRATIONS_DIR"/*/; do
        version=$(basename "$dir")
        if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then echo "$version"; fi
    done | sort -V)

    for version in $versions; do
        version_newer "$version" "$from" || continue
        dir="$MIGRATIONS_DIR/$version"

        local on_disk ordered
        on_disk=$(cd "$dir" && for file in *.sql; do if [ -f "$file" ]; then echo "$file"; fi; done | LC_ALL=C sort)
        pinned=$(migration_pinned_order "$version")
        if [ -n "$pinned" ]; then
            if [ "$(LC_ALL=C sort <<< "$pinned")" != "$on_disk" ]; then
                echo "migrations/$version is pinned in scripts/lib-migrations.sh, and its files there and on disk differ:" >&2
                diff <(LC_ALL=C sort <<< "$pinned") <(printf '%s\n' "$on_disk") | sed -n 's/^</    pinned only:/p; s/^>/    on disk only:/p' >&2 || true
                echo "A shipped folder does not take new files. Put a new migration under the release that ships it." >&2
                return 1
            fi
            ordered=$pinned
        else
            ordered=$on_disk
        fi

        local version_core=() version_module=() version_down=() matched=()
        while IFS= read -r file; do
            [ -n "$file" ] || continue
            case "$file" in
                rollback-*) version_down+=("$dir/$file") ;;
                *)
                    if grep -qxF "$dir/$file" <<< "$modules"; then
                        version_module+=("$dir/$file")
                    else
                        version_core+=("$dir/$file")
                    fi ;;
            esac
        done <<< "$ordered"

        core_up+=(${version_core[@]+"${version_core[@]}"})
        module_up+=(${version_module[@]+"${version_module[@]}"})

        if [ -n "$pinned" ]; then
            down+=(${version_down[@]+"${version_down[@]}"})
            continue
        fi

        forward=(${version_core[@]+"${version_core[@]}"} ${version_module[@]+"${version_module[@]}"})
        local i rollback
        for ((i = ${#forward[@]} - 1; i >= 0; i--)); do
            name=$(basename "${forward[$i]}")
            rollback="$dir/rollback-$name"
            if [ -f "$rollback" ]; then
                down+=("$rollback")
                matched+=("$rollback")
            fi
        done
        for rollback in ${version_down[@]+"${version_down[@]}"}; do
            printf '%s\n' ${matched[@]+"${matched[@]}"} | grep -qxF "$rollback" || down+=("$rollback")
        done
    done

    # Rollbacks were gathered oldest folder first; they run newest folder first.
    local folders_down=() last="" group=()
    for file in ${down[@]+"${down[@]}"}; do
        version=$(basename "$(dirname "$file")")
        if [ "$version" != "$last" ] && [ "${#group[@]}" -gt 0 ]; then
            folders_down=("${group[@]}" ${folders_down[@]+"${folders_down[@]}"})
            group=()
        fi
        group+=("$file")
        last=$version
    done
    if [ "${#group[@]}" -gt 0 ]; then folders_down=("${group[@]}" ${folders_down[@]+"${folders_down[@]}"}); fi

    for file in ${core_up[@]+"${core_up[@]}"}; do echo "core $file"; done
    for file in ${module_up[@]+"${module_up[@]}"}; do echo "module $file"; done
    for file in ${folders_down[@]+"${folders_down[@]}"}; do echo "rollback $file"; done
}

# The plan as the psql lines an operator runs, under one heading for each part.
migration_plan_psql() { # $1 = the version the database started on
    local plan kind path flags heading=""
    plan=$(migration_plan "$1") || return 1
    while read -r kind path; do
        [ -n "$kind" ] || continue
        if [ "$kind" != "$heading" ]; then
            if [ -n "$heading" ]; then echo; fi
            case "$kind" in
                core) echo "# core files, applied first, with the API stopped" ;;
                module) echo "# module files, after the core files; a host without that module can skip its file" ;;
                rollback) echo "# rollback, newest first, only when going back to $1" ;;
            esac
            heading=$kind
        fi
        flags="--single-transaction "
        migration_is_transactional "$path" || flags=""
        echo "psql \"\$DATABASE_URL\" -v ON_ERROR_STOP=1 ${flags}-f $path"
    done <<< "$plan"
}

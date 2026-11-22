#!/usr/bin/env bash
# Refuse a deploy whose schema changes the target database will not accept.
#
# The app runs AutoCreate.CreateOnly in production and on playground: a missing table is created,
# an existing one is never altered. So a release carrying a delta on a table that already exists
# starts, reaches ApplyMartenSchemaAsync, throws SchemaMigrationException and crash-loops. The
# database is untouched and the old container is already gone.
#
# That has now happened twice. 3.14.0 crash-looped on a new Marten index (the comment on
# deploy-playground in release.yml records it), and 4.2.0 crash-looped on the mt_doc_users delta
# from the normalised identity work, after four images had been built and pushed.
#
# db-assert is the check that was already on the image and not in the deploy path. Exit 0 means
# every declared object exists; non-zero prints the statements still outstanding, which is the
# list of what migrations/<version>/ has to apply first.
#
#   scripts/assert-schema-current.sh <compose-dir> <service>
#
# Runs the CURRENT image of <service>, so the answer is about the build being deployed rather than
# the one already running. Where the compose file has a postgres-extensions job (6.0 and later) it
# runs first, because the API connects as the role that job creates. That job can change table
# ownership, so this script is not read-only.

set -euo pipefail

COMPOSE_DIR="${1:?usage: assert-schema-current.sh <compose-dir> <service>}"
SERVICE="${2:?usage: assert-schema-current.sh <compose-dir> <service>}"

cd "${COMPOSE_DIR}"

echo "==> db-assert against the ${SERVICE} service's database"

# From 6.0 the API connects as the app role. The postgres-extensions job runs as the owner: it creates
# the role and makes it the owner of the tables. On a 5.x volume the role does not exist yet, and until
# the job reruns, a migration's DDL fails as a non-owner. So the job runs first, where the compose file has one. It starts postgres.
if ! SERVICES="$(docker compose config --services)"; then
    echo "!! docker compose config failed, so the compose file does not resolve. Fix that first (for example DB_APP_PASSWORD unset in .env)." >&2
    exit 1
fi
if grep -qx postgres-extensions <<< "${SERVICES}"; then
    echo "==> running the postgres-extensions job (creates or updates the app role)"
    if ! docker compose run --rm postgres-extensions; then
        echo "!! the postgres-extensions job did not exit 0, so the ${SERVICE} service cannot connect as the app role." >&2
        echo "!! Check DB_PASSWORD and DB_APP_PASSWORD in .env, and docker compose logs postgres-extensions. Stopped before db-assert." >&2
        exit 1
    fi
fi

# --no-deps so this does not start postgres if it is down: a database that is not there is a
# different failure and should read as one. `run` gives the command the service's own environment,
# so it checks the connection string the deploy will use and nothing has to be passed in here.
if docker compose run --rm --no-deps "${SERVICE}" db-assert; then
    echo "==> schema is current, safe to deploy"
    exit 0
fi

cat >&2 <<'EOF'

!! db-assert found outstanding schema changes.

The deploy was stopped BEFORE the running container was replaced, so the site is still up on the
previous build. No migration ran and no schema was changed. Where the compose file has the
postgres-extensions job, that job did run first: it may have created or updated the app role,
changed table ownership and taken short locks.

AutoCreate.CreateOnly will not apply these. Run the migrations for the version being deployed. From
4.6.0 the image does it, in order, and records each one (docs/migrations.md). Stop the service
first: a migration must not run under a serving API.

    docker compose stop <service>
    docker compose run --rm postgres-extensions    (6.0 and later, before any db-migrate)
    docker compose run --rm --no-deps <service> db-migrate

With an older image, apply each file by hand:

    psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/<version>/<file>.sql

A file that needs a superuser (docs/deploy-in-production.md, Database roles) takes the search path pin
in front: PGOPTIONS='-c search_path=pg_catalog,pg_temp' psql ...

Check the version's migrations directory for the order and for any file that must run outside a
transaction (CREATE INDEX CONCURRENTLY cannot be wrapped in one). Then deploy again.
EOF
exit 1

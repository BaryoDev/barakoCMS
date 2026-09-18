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
# the one already running.

set -euo pipefail

COMPOSE_DIR="${1:?usage: assert-schema-current.sh <compose-dir> <service>}"
SERVICE="${2:?usage: assert-schema-current.sh <compose-dir> <service>}"

cd "${COMPOSE_DIR}"

echo "==> db-assert against the ${SERVICE} service's database"

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
previous build and the database is untouched.

AutoCreate.CreateOnly will not apply these. Run the migrations for the version being deployed:

    psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/<version>/<file>.sql

Check the version's migrations directory for the order and for any file that must run outside a
transaction (CREATE INDEX CONCURRENTLY cannot be wrapped in one). Then deploy again.
EOF
exit 1

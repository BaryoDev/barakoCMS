-- The seed_markers table, for a database that existed before it did (#1165).
--
-- One row per one-time step of the data seeder that has already run in this deployment, so the next
-- start does not run it again and undo an administrator's later choice. Single-tenanted, like the
-- roles those steps change, so there is no tenant_id column. Empty on arrival: the row appears
-- the first time the step runs.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the table
-- as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/seed-markers.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_seed_markers (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_seed_markers_id PRIMARY KEY (id)
);

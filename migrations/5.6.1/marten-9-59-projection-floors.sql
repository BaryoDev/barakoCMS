-- Marten 9.59.0 adds mt_event_projection_floors, a per-database table that records the sequence an
-- async projection starts from (projection start floors, JasperFx 2.102.0).
--
-- Why this file has to exist: the app runs AutoCreate.CreateOnly, so a fresh install gets the table
-- at startup, and db-assert reports it missing on an upgraded database until this runs. Run it with
-- the API stopped, like every upgrade file:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.6.1/marten-9-59-projection-floors.sql
--
-- The statement is what db-assert prints for this upgrade, with IF NOT EXISTS so a re-run is a
-- no-op. Types and defaults are Marten's, verbatim.
--
-- Safe to run twice. rollback-marten-9-59-projection-floors.sql drops the table.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_event_projection_floors (
    name         varchar                  NOT NULL,
    tenant_id    varchar                  NOT NULL,
    floor        bigint                   NOT NULL,
    last_updated timestamp with time zone NULL DEFAULT (transaction_timestamp()),
    CONSTRAINT pkey_mt_event_projection_floors_name_tenant_id PRIMARY KEY (name, tenant_id)
);

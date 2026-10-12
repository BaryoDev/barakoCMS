-- The stations table, for a database that existed before station keys did.
--
-- One row per station: a shared device such as a kiosk or a queue display that signs in with a
-- bsta_ key instead of a person's account. The row holds a hash of the key, never the key, plus the
-- tenant the station belongs to and the capabilities it was given. Global, like API keys, so there
-- is no tenant_id column; the tenant is kept as data. Empty on arrival: rows appear when an admin
-- creates a station.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the table
-- as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added to the indexes
-- so the file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.9.0/stations.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_stations (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_stations_id PRIMARY KEY (id)
);

-- The key lookup at sign in, and what refuses two stations with the same key.
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_stations_uidx_key_hash
    ON public.mt_doc_stations USING btree ((data ->> 'KeyHash'));

CREATE INDEX IF NOT EXISTS mt_doc_stations_idx_tenant_slug
    ON public.mt_doc_stations USING btree ((data ->> 'TenantSlug'));

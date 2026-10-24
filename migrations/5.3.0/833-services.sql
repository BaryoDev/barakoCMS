-- The Services module's registry table (#833), for a database that existed before the module did.
--
-- One row per outside service, deployment-wide, so there is no tenant_id column. The unique index
-- on Name is what makes two registrations of one name race to a single winner. Empty on arrival.
-- Shipped from the Services module, so it runs only where the module is enabled.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the
-- table as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added so the
-- file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/833-services.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_service_registrations (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_service_registrations_id PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_service_registrations_uidx_name
    ON public.mt_doc_service_registrations USING btree ((data ->> 'Name'));

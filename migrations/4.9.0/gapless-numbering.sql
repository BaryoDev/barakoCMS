-- The two numbering tables (#584), for a database that existed before gapless numbering did.
--
-- barako_number_counters holds one row per tenant, series and period: the last number handed out.
-- The allocator takes the next number by updating that row inside the caller's transaction, so a
-- rolled back write gives its number back and the series has no gaps. mt_doc_external_number_uses
-- records a number that came from outside (a paper receipt book, an older system) so the allocator
-- never hands it out again. Both are per tenant. Both are empty on arrival: rows appear when a
-- module first allocates or records a number.
--
-- CreateOnly would create both on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the tables
-- as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added to the indexes
-- so the file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.9.0/gapless-numbering.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.barako_number_counters (
    tenant_id     varchar    NOT NULL,
    series        varchar    NOT NULL,
    period        varchar    NOT NULL,
    last_value    bigint     NOT NULL,
    CONSTRAINT pkey_barako_number_counters_tenant_id_series_period PRIMARY KEY (tenant_id, series, period)
);

CREATE TABLE IF NOT EXISTS public.mt_doc_external_number_uses (
    tenant_id           varchar                     NOT NULL DEFAULT '*DEFAULT*',
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_external_number_uses_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS mt_doc_external_number_uses_idx_series
    ON public.mt_doc_external_number_uses USING btree ((data ->> 'Series'));

CREATE INDEX IF NOT EXISTS mt_doc_external_number_uses_idx_number
    ON public.mt_doc_external_number_uses USING btree ((CAST(data ->> 'Number' as bigint)));

-- Row level security is off for this table, the same as every other conjoined document type here.
-- Tenancy at the database is opt in (see migrations/tenancy/001-app-role.sql) and db-assert reports
-- a policy this build does not declare, so these three lines are what make the assert pass on a
-- database where something once enabled it.
DROP POLICY IF EXISTS marten_tenant_isolation ON public.mt_doc_external_number_uses;
ALTER TABLE public.mt_doc_external_number_uses NO FORCE ROW LEVEL SECURITY;
ALTER TABLE public.mt_doc_external_number_uses DISABLE ROW LEVEL SECURITY;

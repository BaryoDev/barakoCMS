-- The tenant settings table (#896), for a database that existed before it did.
--
-- One row per tenant: the values the tenant set for the sections its modules declare, and the
-- protected (encrypted) values for keys marked secret. The row id is derived from the tenant slug,
-- so a tenant never has two. Global, like the tenants it belongs to, so there is no tenant_id column.
--
-- Nothing existing reads or writes the table, so creating it moves no data. CreateOnly would create
-- it on first boot, but production applies this file ahead of the upgrade.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/896-tenant-settings.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_tenant_settings (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_tenant_settings_id PRIMARY KEY (id)
);

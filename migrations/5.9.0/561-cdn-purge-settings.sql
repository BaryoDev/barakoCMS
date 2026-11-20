-- The CDN purge settings table (#561), for a database that existed before it did.
--
-- One row per tenant: the provider, target id and enabled flag the tenant set for CDN purge, and
-- the protected (encrypted) token. The row id is derived from the tenant slug, so a tenant never
-- has two. Global, like the tenants it belongs to, so there is no tenant_id column.
--
-- Nothing existing reads or writes the table, so creating it moves no data. CreateOnly would create
-- it on first boot, but production applies this file ahead of the upgrade.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.9.0/561-cdn-purge-settings.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_cdn_purge_settings (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_cdn_purge_settings_id PRIMARY KEY (id)
);

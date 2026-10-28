-- The ExternalAuth module's external_provider_settings table, for a database that existed before it did (#915).
--
-- One row per tenant that limits which external providers its people sign in with. Global, like
-- the users it reaches, so there is no tenant_id column. The id is the tenant's slug, so a tenant
-- has one list and there is no other index. Empty on arrival: a row appears when an administrator
-- sets a list at PUT /api/tenants/external-providers, and a tenant with no row uses the
-- deployment's providers.
--
-- CreateOnly would create it on first boot; this file exists so db-assert passes before the deploy
-- instead of reporting the table as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/915-external-auth-provider-settings.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_external_provider_settings (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_external_provider_settings_id PRIMARY KEY (id)
);

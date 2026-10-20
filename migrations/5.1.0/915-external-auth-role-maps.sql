-- The ExternalAuth module's external_role_maps table, for a database that existed before it did (#915).
--
-- One row per tenant that maps an identity provider claim to roles. Global, like the users it
-- reaches, so there is no tenant_id column. The id is the tenant's slug, so a tenant has one map
-- and there is no other index. Empty on arrival: a row appears when an administrator sets a map at
-- PUT /api/tenants/external-role-map.
--
-- CreateOnly would create it on first boot; this file exists so db-assert passes before the deploy
-- instead of reporting the table as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/915-external-auth-role-maps.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_external_role_maps (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_external_role_maps_id PRIMARY KEY (id)
);

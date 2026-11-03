-- The service principals table (#906), for a database that existed before it did.
--
-- A service principal is an identity for a machine caller. An API key can act as one, so the key
-- takes its roles from the principal and keeps working when the person who made it leaves. Global,
-- like the API keys that point at it, so there is no tenant_id column. A principal belongs to one
-- tenant through its TenantSlug.
--
-- The new ServicePrincipalId and ReadOnly fields on api keys are JSON properties, so the api keys
-- table needs no change. A key without them acts as its creator and is not read-only, as before.
--
-- CreateOnly would create the table on first boot, so this file is for an operator who applies the
-- schema by hand.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/906-service-principals.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_service_principals (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_service_principals_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS mt_doc_service_principals_idx_tenant_slug
    ON public.mt_doc_service_principals USING btree ((data ->> 'TenantSlug'));

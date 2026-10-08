-- The ExternalAuth module's oidc_used_nonces table, for a database that existed before it did.
--
-- One row per nonce the id token grant (POST /api/auth/oidc/{name}/id-token) has accepted, kept
-- until the token it came in has expired, so the nonce cannot be used again. Global, like the
-- users the grant signs in, so there is no tenant_id column. The id is a hash of issuer and nonce,
-- and the primary key is what refuses a second use: the module inserts, and a duplicate fails.
-- There is no other index. Expired rows are deleted by the grant itself. Empty on arrival: rows
-- appear once a provider lists IdTokenAudiences and an app uses the grant.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the table
-- as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.8.0/external-auth-used-nonces.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_oidc_used_nonces (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_oidc_used_nonces_id PRIMARY KEY (id)
);

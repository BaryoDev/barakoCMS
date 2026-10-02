-- The ExternalAuth module's external_identities table, for a database that existed before it did.
--
-- One row per provider account that has signed in through an OpenID Connect provider, tying its
-- issuer and subject to a local user. Global, like the users it points at, so there is no tenant_id
-- column. The id is a hash of issuer and subject, so one provider account has one row, and there
-- is no other index. The module writes the row with an upsert, so linking an account again
-- replaces its row. Empty on arrival: rows appear as people sign in through a provider configured
-- under Oidc:Providers.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the table
-- as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/external-auth-identities.sql
--
-- Safe to run twice.

CREATE TABLE IF NOT EXISTS public.mt_doc_external_identities (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_external_identities_id PRIMARY KEY (id)
);

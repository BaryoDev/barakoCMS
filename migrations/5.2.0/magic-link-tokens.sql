-- The magic_link_tokens table, for a database that existed before it did.
--
-- One row per magic sign-in link that has been issued. Global: keyed by the visitor's email, with the tenant it signs in to kept as data, so there is no tenant_id column. Looked up by email and swept by expiry. Empty on arrival.
--
-- CreateOnly would create it on first boot; this file exists so db-assert passes before the deploy
-- instead of reporting the table as outstanding.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/magic-link-tokens.sql
--
-- Safe to run twice. rollback-magic-link-tokens.sql drops the table.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_magic_link_tokens (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_magic_link_tokens_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS mt_doc_magic_link_tokens_idx_email
    ON public.mt_doc_magic_link_tokens USING btree ((data ->> 'Email'));

CREATE INDEX IF NOT EXISTS mt_doc_magic_link_tokens_idx_expires_at
    ON public.mt_doc_magic_link_tokens USING btree ((public.mt_immutable_timestamp(data ->> 'ExpiresAt')));

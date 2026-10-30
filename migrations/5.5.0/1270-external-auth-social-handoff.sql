-- The ExternalAuth module's social_handoff_codes and facebook_deletion_requests tables, for a
-- database that existed before they did (#1270).
--
-- social_handoff_codes: one row per sign-in waiting to be collected with POST /api/auth/social/exchange,
-- written only when ExternalAuth:CodeHandoff is on. The id is the SHA-256 of the code, and the
-- row lives 60 seconds. The index on ExpiresAt serves the purge, which deletes expired rows in
-- bounded batches. Global, like the users it signs in, so there is no tenant_id column.
--
-- facebook_deletion_requests: one row per Facebook data deletion request answered, keyed by the
-- confirmation code Facebook was given, so the status URL can report it. Global too.
--
-- Both are empty on arrival. CreateOnly would create them on first boot; this file exists so
-- db-assert passes before the deploy instead of reporting the tables as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/1270-external-auth-social-handoff.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_social_handoff_codes (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_social_handoff_codes_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS mt_doc_social_handoff_codes_idx_expires_at
    ON public.mt_doc_social_handoff_codes USING btree ((public.mt_immutable_timestamp(data ->> 'ExpiresAt')));

CREATE TABLE IF NOT EXISTS public.mt_doc_facebook_deletion_requests (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_facebook_deletion_requests_id PRIMARY KEY (id)
);

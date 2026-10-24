-- The account content types table (#945), for a database that existed before it did.
--
-- A content type owned by an account and used by every tenant in it. Global, like accounts, so there
-- is no tenant_id column. The unique index on the account and the name refuses two definitions of
-- one name in an account. Empty on arrival. Core ships this file.
--
-- CreateOnly would create it on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the
-- table as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added so the
-- file can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/945-account-content-types.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_account_content_types (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_account_content_types_id PRIMARY KEY (id)
);

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_account_content_types_uidx_account_idname
    ON public.mt_doc_account_content_types USING btree ((CAST(data ->> 'AccountId' as uuid)), (data ->> 'Name'));

-- The accounts and account_managers tables, for a database that existed before accounts did (#898).
--
-- An account owns tenants, for an agency or reseller running tenants for others. An account manager
-- row lets one user manage the tenants of one account. Both are global, like users, so there is no
-- tenant_id column. Empty on arrival: rows appear when an administrator creates an account or grants
-- a manager.
--
-- CreateOnly would create them on first boot, since a missing table is a creation and not an
-- alteration; this file exists so db-assert passes before the deploy instead of reporting the tables
-- as outstanding. The statements are what db-assert reports, with IF NOT EXISTS added so the file
-- can be run twice.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/898-accounts.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_accounts (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_accounts_id PRIMARY KEY (id)
);

-- Refuses two accounts with the same slug.
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_accounts_uidx_slug
    ON public.mt_doc_accounts USING btree ((data ->> 'Slug'));

CREATE TABLE IF NOT EXISTS public.mt_doc_account_managers (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_account_managers_id PRIMARY KEY (id)
);

-- The lookup of which accounts a user manages.
CREATE INDEX IF NOT EXISTS mt_doc_account_managers_idx_user_id
    ON public.mt_doc_account_managers USING btree ((CAST(data ->> 'UserId' as uuid)));

-- Refuses granting the same user the same account twice.
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_account_managers_uidx_account_iduser_id
    ON public.mt_doc_account_managers USING btree ((CAST(data ->> 'AccountId' as uuid)), (CAST(data ->> 'UserId' as uuid)));

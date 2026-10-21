-- Undoes migrations/5.2.0/898-accounts.sql, for a rollback to a release before accounts.
--
-- An earlier release does not declare these tables. It would still boot beside them, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping them puts the
-- database back to what that release built.
--
-- WHAT IS LOST: every account, its branding and every manager grant. Tenants stay, and an earlier
-- release treats them all as one group. Each tenant's AccountId is removed too, since it would point
-- at an account that no longer exists; a tenant with none belongs to the default account. After
-- upgrading again, accounts and grants have to be created again.
--
-- The indexes go with the tables, so there is nothing to drop separately.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-898-accounts.sql
--
-- Safe to run twice.

DO $$
BEGIN
    IF to_regclass('public.mt_doc_tenants') IS NOT NULL THEN
        UPDATE public.mt_doc_tenants SET data = data - 'AccountId' WHERE data ? 'AccountId';
    END IF;
END
$$;

DROP TABLE IF EXISTS public.mt_doc_account_managers;
DROP TABLE IF EXISTS public.mt_doc_accounts;

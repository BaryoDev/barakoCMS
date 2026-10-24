-- Undoes migrations/5.3.0/945-account-content-types.sql, for a rollback to a release before account content types.
--
-- An earlier release does not declare this table. Dropping it puts the database back to what that
-- release built. The indexes go with the tables.
--
-- WHAT IS LOST: every account content type definition. Entries of those types stay, but nothing validates them against a definition.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/rollback-945-account-content-types.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_account_content_types;

-- Undoes migrations/5.3.0/98-locale-settings.sql, for a rollback to a release before locale settings.
--
-- An earlier release does not declare this table. Dropping it puts the database back to what that
-- release built. The indexes go with the tables.
--
-- WHAT IS LOST: every tenant's locale settings. They have to be saved again after upgrading.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/rollback-98-locale-settings.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_locale_settings;

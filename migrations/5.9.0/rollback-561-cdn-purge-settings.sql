-- Undoes migrations/5.9.0/561-cdn-purge-settings.sql, for a rollback to a release before CDN purge settings.
--
-- An earlier release does not declare this table and nothing in it reads the rows.
--
-- WHAT IS LOST: every tenant's CDN purge provider, target id and enabled flag, and the protected
-- token with them. Nothing purges until the settings are entered again.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.9.0/rollback-561-cdn-purge-settings.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_cdn_purge_settings;

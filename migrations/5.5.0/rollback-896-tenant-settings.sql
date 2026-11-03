-- Undoes migrations/5.5.0/896-tenant-settings.sql, for a rollback to a release before tenant settings.
--
-- An earlier release does not declare this table and nothing in it reads the rows.
--
-- WHAT IS LOST: every value a tenant set for a module section, and the protected values with them.
-- Each tenant falls back to the host's settings.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-896-tenant-settings.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_tenant_settings;

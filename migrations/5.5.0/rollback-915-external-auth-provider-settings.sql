-- Undoes migrations/5.5.0/915-external-auth-provider-settings.sql, for a rollback to a release
-- before per-tenant provider lists.
--
-- WHAT IS LOST: every tenant's list. Each tenant goes back to the deployment's providers. After
-- upgrading again, each tenant has to set its list again.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-915-external-auth-provider-settings.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_external_provider_settings;

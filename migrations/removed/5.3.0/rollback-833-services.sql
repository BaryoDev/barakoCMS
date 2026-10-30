-- Undoes migrations/5.3.0/833-services.sql, for a rollback to a release before the Services module.
--
-- An earlier release does not declare this table. Dropping it puts the database back to what that
-- release built. The indexes go with the tables.
--
-- WHAT IS LOST: every registered service and its last heartbeat. Services register again after the upgrade.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/rollback-833-services.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_service_registrations;

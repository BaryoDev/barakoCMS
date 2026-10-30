-- Undoes migrations/5.2.0/notifications.sql, for a rollback to a release before the Notifications module.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every notification of every tenant, read or not. Nothing else reads the table.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-notifications.sql

DROP TABLE IF EXISTS public.mt_doc_notifications;

-- Undoes migrations/removed/5.2.0/scheduled-entries.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every scheduled change that has not fired yet. Those changes never happen.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/rollback-scheduled-entries.sql

DROP TABLE IF EXISTS public.mt_doc_scheduled_entries;

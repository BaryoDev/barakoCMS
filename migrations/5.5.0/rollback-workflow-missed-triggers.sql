-- Undoes migrations/5.5.0/workflow-missed-triggers.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: the events waiting to be replayed. A run that was not queued for one of them
-- stays unqueued.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-workflow-missed-triggers.sql

DROP TABLE IF EXISTS public.mt_doc_workflow_missed_triggers;

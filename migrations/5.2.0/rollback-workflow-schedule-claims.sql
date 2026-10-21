-- Undoes migrations/5.2.0/workflow-schedule-claims.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: the record of claimed schedule slots. A slot already run could run again once.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-workflow-schedule-claims.sql

DROP TABLE IF EXISTS public.mt_doc_workflow_schedule_claims;

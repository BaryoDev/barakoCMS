-- Undoes migrations/5.7.2/workflow-run-status-tenant-index.sql, for a rollback to a release before it.
--
-- WHAT IS LOST: nothing. The index only speeds a query up. An older build does not declare it, so a
-- schema check there would list it as extra.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.7.2/rollback-workflow-run-status-tenant-index.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP INDEX IF EXISTS public.mt_doc_workflow_runs_idx_status_tenant;

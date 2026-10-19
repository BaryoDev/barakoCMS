-- Undoes migrations/5.1.0/workflow-run-workflow-index.sql, for a rollback to a release before the
-- workflow filter. An older build does not declare the index, so db-assert there would list it as
-- extra.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/rollback-workflow-run-workflow-index.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP INDEX IF EXISTS public.mt_doc_workflow_runs_idx_workflow_updated_at;

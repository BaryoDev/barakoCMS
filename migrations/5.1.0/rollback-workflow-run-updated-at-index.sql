-- Undoes migrations/5.1.0/workflow-run-updated-at-index.sql, for a rollback to a release before
-- the changed-since cursor. An older build does not declare the index, so db-assert there would
-- list it as extra. The UpdatedAt value stays in each run's document, where an older build ignores it.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/rollback-workflow-run-updated-at-index.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP INDEX IF EXISTS public.mt_doc_workflow_runs_idx_updated_at;

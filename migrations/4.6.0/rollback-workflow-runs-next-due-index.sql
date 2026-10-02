-- Undoes migrations/4.6.0/workflow-runs-next-due-index.sql, for a rollback to a release before it.
-- An older build does not declare the index, so db-assert there would list it as extra.
--
-- Nothing else to undo. An older build ignores the NextDueAt value on a stored run and drops it the
-- next time it writes the run.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/rollback-workflow-runs-next-due-index.sql
--
-- Safe to run twice.

DROP INDEX IF EXISTS public.mt_doc_workflow_runs_idx_next_due_at;

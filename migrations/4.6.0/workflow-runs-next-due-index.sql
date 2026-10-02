-- Index workflow_runs.NextDueAt on a database that already existed before the runner asked for due
-- runs in the query.
--
-- Core declares this index, and Marten creates it only where workflow_runs does not yet exist: the
-- app runs AutoCreate.CreateOnly, which adds a missing object and never alters one that is there.
-- Without this file an upgraded database still works, since the runner's query is correct without
-- the index, and db-assert reports the index as missing.
--
-- No backfill. A run stored before the upgrade has no NextDueAt, the runner reads that as due, and
-- the value is written the first time the runner looks at the run.
--
-- Run it with the API stopped, like every upgrade file.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/workflow-runs-next-due-index.sql
--
-- Safe to run twice.

-- The expression has to be Marten's, or this is a different index under the right name and every
-- start-up schema assertion asks to drop and recreate it. WorkflowRunsNextDueIndexMigrationTests
-- compares this file to the index Marten builds.
CREATE INDEX IF NOT EXISTS mt_doc_workflow_runs_idx_next_due_at
    ON public.mt_doc_workflow_runs USING btree ((public.mt_immutable_timestamptz(data ->> 'NextDueAt')));

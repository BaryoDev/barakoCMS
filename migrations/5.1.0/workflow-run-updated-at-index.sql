-- Index workflow_runs.UpdatedAt on a database that existed before runs carried one (#1164).
--
-- GET /api/workflow-runs?changedSince= orders and filters runs on UpdatedAt, and a console polls it
-- every couple of seconds. Core declares the index, and Marten creates it only where the table does
-- not yet exist: the app runs AutoCreate.CreateOnly, which adds a missing object and never alters
-- one that is there. Without this file an upgraded database still answers, but every poll scans the
-- table, and db-assert reports the index as missing.
--
-- Runs written before the upgrade have no UpdatedAt, so they are absent from the index and from
-- every changed-since answer until they are next written. That is the intended reading: they have
-- not changed.
--
-- Run it with the API stopped, like every upgrade file.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/workflow-run-updated-at-index.sql
--
-- Safe to run twice. rollback-workflow-run-updated-at-index.sql drops the index.
-- barako:rerunnable

CREATE INDEX IF NOT EXISTS mt_doc_workflow_runs_idx_updated_at
    ON public.mt_doc_workflow_runs USING btree ((public.mt_immutable_timestamptz(data ->> 'UpdatedAt')));

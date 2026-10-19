-- Index workflow_runs on (WorkflowDefinitionId, UpdatedAt) on a database that existed before it (#1165).
--
-- GET /api/workflow-runs?workflowId= filters on the workflow and, with a changedSince cursor, orders
-- on UpdatedAt, and GET /api/workflows/health reads the runs of every workflow on its page. Core
-- declares the index, and Marten creates it only where the table does not yet exist: the app runs
-- AutoCreate.CreateOnly, which adds a missing object and never alters one that is there. Without
-- this file an upgraded database still answers, but those reads scan the table, and db-assert
-- reports the index as missing.
--
-- Run it with the API stopped, like every upgrade file.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/workflow-run-workflow-index.sql
--
-- Safe to run twice. rollback-workflow-run-workflow-index.sql drops the index.
-- barako:rerunnable

CREATE INDEX IF NOT EXISTS mt_doc_workflow_runs_idx_workflow_updated_at
    ON public.mt_doc_workflow_runs USING btree ((CAST(data ->> 'WorkflowDefinitionId' as uuid)), (public.mt_immutable_timestamptz(data ->> 'UpdatedAt')));

-- Index workflow_runs for the run list's status counts: workflow, status and tenant.
--
-- GET /api/workflow-runs counts the runs of each status for the caller's tenant, narrowed by
-- workflowId or contentId when one is given, reading at most StatusCountCap + 1 ids per status. Every
-- column the count filters on is an equality, so the index holds them all and Postgres can stop at the
-- cap inside one index range. Core declares it, and Marten creates it only where the table does not
-- yet exist: the app runs AutoCreate.CreateOnly, which adds a missing object and never alters one that
-- is there. Without this file an upgraded database still answers, but the count reads the table.
--
-- CONCURRENTLY so it does not block writes to mt_doc_workflow_runs while a long history is read. That
-- means it cannot run inside a transaction, and it waits for every transaction older than itself, so
-- run it with the API stopped like every upgrade file (docs/migrations.md). If an earlier attempt was
-- cancelled it leaves an invalid index under this name, which IF NOT EXISTS keeps: drop it with
-- rollback-workflow-run-workflow-status-tenant-index.sql and run this file again.
--
--   psql "$DATABASE_URL" -f migrations/5.7.2/workflow-run-workflow-status-tenant-index.sql
--
-- Safe to run twice. rollback-workflow-run-workflow-status-tenant-index.sql drops it.

-- barako:skip-when: select exists (select 1 from pg_index x join pg_class c on c.oid = x.indexrelid join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = 'mt_doc_workflow_runs_idx_workflow_status_tenant' and x.indisvalid)
-- barako:no-transaction

CREATE INDEX CONCURRENTLY IF NOT EXISTS mt_doc_workflow_runs_idx_workflow_status_tenant
    ON public.mt_doc_workflow_runs USING btree ((CAST(data ->> 'WorkflowDefinitionId' as uuid)), (CAST(data ->> 'Status' as integer)), tenant_id);

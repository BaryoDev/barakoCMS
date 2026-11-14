-- Undoes migrations/5.6.1/event-tenant-timestamp-index.sql, for a rollback to a release before it.
--
-- WHAT IS LOST: nothing. The index only speeds a query up.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.6.1/rollback-event-tenant-timestamp-index.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP INDEX IF EXISTS public.mt_events_idx_tenant_timestamp;

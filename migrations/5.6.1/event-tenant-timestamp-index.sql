-- Indexes mt_events on the tenant and the timestamp (#1275), for the activity feed.
--
-- GET /api/activity reads content events for one tenant by time: newest first inside its window,
-- and oldest first after a cursor or a since older than the window. mt_events has its keys on the
-- sequence and the stream, and nothing on the timestamp, so every such read scanned the tenant's
-- events. The index is (tenant_id, timestamp, seq_id), which serves both orders as a forward or a
-- backward scan with the tie break on the sequence in the index itself.
--
-- Marten has no setting for an extra index on mt_events, so core does not declare this one and a
-- fresh database does not get it from the first start. Core tells Marten to ignore it
-- (Events.IgnoreIndex), so db-assert passes and a schema apply does not drop it. Run db-migrate
-- after the first start of a new database as well as on an upgrade. Without the index the feed still answers, and reads past the
-- window scan the tenant's events.
--
-- CONCURRENTLY so it does not block writes to mt_events while a long history is read. That means it
-- cannot run inside a transaction, and it waits for every transaction older than itself, so run it
-- with the API stopped like every upgrade file (docs/migrations.md). Existing rows are read once
-- and none is changed. If an earlier attempt was cancelled it leaves an invalid index under this
-- name, which IF NOT EXISTS keeps: drop it with rollback-event-tenant-timestamp-index.sql and run
-- this file again.
--
--   psql "$DATABASE_URL" -f migrations/5.6.1/event-tenant-timestamp-index.sql
--
-- Safe to run twice. rollback-event-tenant-timestamp-index.sql drops the index.

-- For db-migrate (docs/migrations.md). It records this file without running it when the index is there and valid.
-- barako:skip-when: select exists (select 1 from pg_index x join pg_class c on c.oid = x.indexrelid join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = 'mt_events_idx_tenant_timestamp' and x.indisvalid)
-- barako:no-transaction

CREATE INDEX CONCURRENTLY IF NOT EXISTS mt_events_idx_tenant_timestamp
    ON public.mt_events USING btree (tenant_id, "timestamp", seq_id);

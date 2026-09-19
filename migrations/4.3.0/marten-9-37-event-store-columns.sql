-- Marten 9.30.0 to 9.37.0 adds a compaction watermark and the extended progression tracking
-- columns to the event store, on tables that already exist.
--
-- mt_streams.compacted_version is the watermark CompactStreamAsync records (Marten 9.32.0). The
-- thirteen columns on mt_event_progression are the async daemon's per-shard health: which node holds
-- the shard and its heartbeat, whether it is paused and why, how far behind it is against a warning
-- and a critical threshold, and the sequence, type and tenant of the event that failed it. Those are
-- the reason for taking this version, so the columns are the feature rather than a side effect.
--
-- Why this file has to exist: the app runs AutoCreate.CreateOnly in production and on playground,
-- which creates a missing table and never alters one that is there. A fresh install gets these
-- columns and every upgraded install does not, so the host reaches ApplyMartenSchemaAsync, throws
-- SchemaMigrationException and crash-loops with the previous container already gone. Apply this
-- while the old build is still serving, then deploy.
--
-- Cost: every statement adds a column, and on PostgreSQL 11 and later ADD COLUMN with a constant
-- default is a catalogue change rather than a table rewrite. Production runs 16, so this is fast on
-- an event store of any size and holds only a brief ACCESS EXCLUSIVE lock per statement.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.3.0/marten-9-37-event-store-columns.sql
--
-- Safe to run twice.
--
-- The statements are what db-patch emits for this upgrade, with IF NOT EXISTS added so a re-run is
-- a no-op. Types and defaults are Marten's, verbatim: a column of the right name and the wrong type
-- is a column the start-up assertion asks to change, which CreateOnly then refuses.

ALTER TABLE public.mt_streams
    ADD COLUMN IF NOT EXISTS compacted_version bigint NOT NULL DEFAULT 0;

ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS mode varchar NULL DEFAULT 'none';
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS rebuild_threshold integer NULL DEFAULT 0;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS assigned_node integer NULL DEFAULT 0;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS heartbeat timestamp with time zone NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS agent_status varchar(20) NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS pause_reason text NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS running_on_node integer NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS warning_behind_threshold bigint NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS critical_behind_threshold bigint NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS failure_category varchar(50) NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS failure_event_sequence bigint NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS failure_event_type varchar(500) NULL;
ALTER TABLE public.mt_event_progression
    ADD COLUMN IF NOT EXISTS failure_event_tenant_id varchar(500) NULL;

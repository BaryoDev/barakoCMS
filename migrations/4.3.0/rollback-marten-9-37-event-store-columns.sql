-- Undoes migrations/4.3.0/marten-9-37-event-store-columns.sql, for a rollback to a release before it.
--
-- Needed because the refusal runs both ways. A pre-4.3.0 build asserts its own schema against the
-- database and reports these fourteen columns as columns to drop, so it refuses to start on a
-- database that has been migrated. Proven locally: the 9.30.0 Suite fails db-assert against the
-- migrated database and passes again once this file has run.
--
-- What is lost is operational metadata. The thirteen mt_event_progression columns hold the async
-- daemon's live per-shard health, which the daemon rewrites on its next pass, so dropping them
-- costs nothing an operator cannot get back by starting the new build again.
--
-- mt_streams.compacted_version is the exception, and it is the reason to think before running this.
-- It records how far each stream has been compacted. If stream compaction has been used on this
-- database, the events below that watermark are already gone, and dropping the column leaves a
-- stream whose earlier events are missing with nothing recording that this was deliberate. Do not
-- roll back past this migration on a database where compaction has run. Nothing in barakoCMS calls
-- CompactStreamAsync today, so on a deployment that has only upgraded and rolled back, the column
-- is zero everywhere and dropping it is safe.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.3.0/rollback-marten-9-37-event-store-columns.sql
--
-- Safe to run twice.

ALTER TABLE public.mt_streams
    DROP COLUMN IF EXISTS compacted_version;

ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS mode;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS rebuild_threshold;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS assigned_node;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS heartbeat;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS agent_status;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS pause_reason;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS running_on_node;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS warning_behind_threshold;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS critical_behind_threshold;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS failure_category;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS failure_event_sequence;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS failure_event_type;
ALTER TABLE public.mt_event_progression
    DROP COLUMN IF EXISTS failure_event_tenant_id;

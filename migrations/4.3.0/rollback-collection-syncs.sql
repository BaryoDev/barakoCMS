-- Undoes migrations/4.3.0/collection-syncs.sql, for a rollback to a release before collection syncs.
--
-- An earlier release does not declare this table, and it asserts its own schema at startup, so it
-- reports a table it does not know about as outstanding and refuses to boot while it is there.
-- Dropping it is what lets the older image start.
--
-- WHAT IS LOST: the schedules and field mappings somebody configured. The entries a sync wrote are
-- ordinary content in mt_doc_contents and are NOT touched, so the pages those syncs fill keep
-- serving what the last run left; they simply stop being refreshed. Nothing else records the
-- configuration, so write the syncs down before rolling back, or take a backup first.
--
-- The index goes with the table, so there is nothing to drop separately.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.3.0/rollback-collection-syncs.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_collection_syncs;

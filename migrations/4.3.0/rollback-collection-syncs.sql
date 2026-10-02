-- Undoes migrations/4.3.0/collection-syncs.sql, for a rollback to a release before collection syncs.
--
-- An earlier release does not declare this table. It would still boot beside it, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping it puts the
-- database back to what that release built.
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

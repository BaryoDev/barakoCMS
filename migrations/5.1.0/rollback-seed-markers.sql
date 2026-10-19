-- Undoes migrations/5.1.0/seed-markers.sql, for a rollback to a release before the seeder kept
-- markers.
--
-- An earlier release does not declare this table. It would still boot beside it; dropping it puts
-- the database back to what that release built.
--
-- WHAT IS LOST: which one-time seeder steps have run. After upgrading again, a step runs once more,
-- which can grant back a capability an administrator removed.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.1.0/rollback-seed-markers.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_seed_markers;

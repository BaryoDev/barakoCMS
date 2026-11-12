-- Undoes migrations/5.6.1/marten-9-59-projection-floors.sql, for a rollback to a release before it.
--
-- WHAT IS LOST: any projection start floor recorded in the table. barakoCMS sets none today, so on
-- a deployment that has only upgraded and rolled back the table is empty.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.6.1/rollback-marten-9-59-projection-floors.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP TABLE IF EXISTS public.mt_event_projection_floors;

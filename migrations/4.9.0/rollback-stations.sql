-- Undoes migrations/4.9.0/stations.sql, for a rollback to a release before station keys.
--
-- An earlier release does not declare this table. It would still boot beside it, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping it puts the
-- database back to what that release built.
--
-- WHAT IS LOST: every station and its key. An earlier release cannot authenticate a station anyway,
-- so the devices stop working on the rollback either way. If you upgrade again later, each station
-- has to be created again and its new key entered on the device.
--
-- The indexes go with the table, so there is nothing to drop separately.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.9.0/rollback-stations.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_stations;

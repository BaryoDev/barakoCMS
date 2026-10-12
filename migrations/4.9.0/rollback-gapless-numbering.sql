-- Undoes migrations/4.9.0/gapless-numbering.sql, for a rollback to a release before gapless numbering.
--
-- An earlier release does not declare these tables. It would still boot beside them, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping them puts the
-- database back to what that release built.
--
-- WHAT IS LOST: where every series stands, and the record of numbers taken from outside. The numbers
-- already printed on documents stay on those documents, but if you upgrade again later every series
-- starts over from 1 and will hand out numbers that were already issued. Take a backup first, or
-- keep these two tables.
--
-- The indexes go with their table, so there is nothing to drop separately.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.9.0/rollback-gapless-numbering.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_external_number_uses;
DROP TABLE IF EXISTS public.barako_number_counters;

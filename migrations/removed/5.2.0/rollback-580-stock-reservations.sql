-- Undoes migrations/removed/5.2.0/580-stock-reservations.sql, for a rollback to a release before stock
-- reservations.
--
-- An earlier release does not declare these tables and boots beside them. Dropping them puts the
-- database back to what that release built.
--
-- WHAT IS LOST: every counter's balance, every open hold, and the movement history. Take a backup
-- first, or keep these tables.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/rollback-580-stock-reservations.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.barako_stock_movements;
DROP TABLE IF EXISTS public.barako_stock_holds;
DROP TABLE IF EXISTS public.barako_stock_counters;

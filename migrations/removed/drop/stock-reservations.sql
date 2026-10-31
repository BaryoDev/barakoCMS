-- Drops what the stock reservation primitive created. It is removed in 5.5.0 and core no longer
-- touches these tables, so they are only taken off by running this file.
--
-- WHAT IS LOST: every counter's balance, every open hold, and the movement history. Take a backup
-- first, or keep the tables. If another package now reads these tables, do not run this.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/drop/stock-reservations.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.barako_stock_movements;
DROP TABLE IF EXISTS public.barako_stock_holds;
DROP TABLE IF EXISTS public.barako_stock_counters;

DELETE FROM public.barako_migrations WHERE owner = 'core' AND id = '5.2.0/580-stock-reservations';

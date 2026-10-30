-- Undoes migrations/removed/5.2.0/payments-payable-types.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: Every registered payable type. Payments already created keep their data.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/rollback-payments-payable-types.sql

DROP TABLE IF EXISTS public.mt_doc_payments_payable_types;

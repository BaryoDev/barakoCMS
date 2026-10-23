-- Undoes migrations/5.2.0/payments-attempts.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: Every payment attempt and its status. Reconciling a payment against its provider reference is no longer possible.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-payments-attempts.sql

DROP TABLE IF EXISTS public.mt_doc_payments_attempts;

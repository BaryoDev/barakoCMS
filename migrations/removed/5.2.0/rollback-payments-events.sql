-- Undoes migrations/removed/5.2.0/payments-events.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: The record of handled provider events. A replayed event could be processed a second time.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/rollback-payments-events.sql

DROP TABLE IF EXISTS public.mt_doc_payments_events;

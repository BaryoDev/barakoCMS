-- Undoes migrations/5.2.0/910-inbound-webhook-events.sql.
--
-- WHAT IS LOST: the record of handled webhook events. After upgrading again, a provider's retry of
-- an old event would be handled a second time.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-910-inbound-webhook-events.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_inbound_webhook_events;

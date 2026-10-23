-- Undoes migrations/5.2.0/outbox-messages.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every staged outbound message. Any not yet delivered is never sent.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-outbox-messages.sql

DROP TABLE IF EXISTS public.mt_doc_outbox_messages;

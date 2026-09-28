-- Undoes migrations/4.5.0/email-sent-emails.sql, for a rollback to a release before sends were
-- recorded.
--
-- An earlier release does not declare this table, and it asserts its own schema at startup, so it
-- reports a table it does not know about as outstanding and refuses to boot while it is there.
-- Dropping it is what lets the older image start.
--
-- WHAT IS LOST: which tenant sent which email. The emails themselves were delivered and are not
-- affected. A bounce or complaint Resend reports after the rollback is recorded without a tenant,
-- which is what an earlier release does for every event anyway. Events already recorded keep the
-- tenant in their own document.
--
-- The index goes with the table, so there is nothing to drop separately.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.5.0/rollback-email-sent-emails.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_sent_emails;

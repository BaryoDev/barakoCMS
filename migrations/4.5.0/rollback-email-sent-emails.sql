-- Undoes migrations/4.5.0/email-sent-emails.sql, for a rollback to a release before sends were
-- recorded.
--
-- An earlier release does not declare this table. It would still boot beside it, since a release
-- refuses only an index or column it does not declare on a table it does. Dropping it puts the
-- database back to what that release built.
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

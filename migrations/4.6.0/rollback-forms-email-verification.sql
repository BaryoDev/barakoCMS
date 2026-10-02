-- Undoes migrations/4.6.0/forms-email-verification.sql, for a rollback to a release before forms
-- could verify an email field.
--
-- An earlier release does not declare these tables. It would still boot beside them, since a
-- release refuses only an index or column it does not declare on a table it does. Dropping them
-- puts the database back to what that release built.
--
-- WHAT IS LOST: codes that were sent and not yet used, and the counts behind the per address and
-- per form limits. By default a code lives ten minutes and a count an hour, so a visitor who was
-- half way through asks for a new code. Also lost: for a form that is turned off, the note of which
-- field it verified. For a form that is on, that is not in these tables: it is a field in the form's
-- own row in mt_doc_public_forms, which an earlier release ignores, so a form that verified takes
-- unverified submissions again once the earlier release is running.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/rollback-forms-email-verification.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_form_email_verifications;
DROP TABLE IF EXISTS public.mt_doc_form_email_budgets;

-- Undoes migrations/5.5.0/1270-external-auth-social-handoff.sql, for a rollback to a release
-- before the code handoff and the Facebook data deletion callback.
--
-- WHAT IS LOST: codes waiting to be collected (a person mid sign-in signs in again) and the
-- record of which deletion requests were answered, so a status URL Facebook holds stops resolving.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-1270-external-auth-social-handoff.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_social_handoff_codes;
DROP TABLE IF EXISTS public.mt_doc_facebook_deletion_requests;

-- Undoes migrations/5.9.0/561-pending-cdn-purges.sql, for a rollback to a release before purge on commit.
--
-- An earlier release does not declare this table and nothing in it reads the rows.
--
-- WHAT IS LOST: every CDN purge waiting to be sent. Those purges are not sent, so the CDN may keep
-- serving the old version of an entry until its cache lifetime ends.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.9.0/rollback-561-pending-cdn-purges.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_pending_cdn_purges;

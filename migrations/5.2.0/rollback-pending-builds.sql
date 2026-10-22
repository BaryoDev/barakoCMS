-- Undoes migrations/5.2.0/pending-builds.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every build hook waiting to fire. Those builds are not triggered.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-pending-builds.sql

DROP TABLE IF EXISTS public.mt_doc_pending_builds;

-- Undoes migrations/5.2.0/253-comments.sql, for a rollback to a release before the Comments module.
--
-- An earlier release does not declare this table. It would still boot beside it; dropping it puts
-- the database back to what that release built.
--
-- WHAT IS LOST: every comment, on every record and tenant. There is no way to get them back short of
-- a restore.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-253-comments.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_comments;

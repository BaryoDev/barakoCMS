-- Drops what BarakoCMS.Comments created. The package is removed in 5.5.0 and core no longer
-- touches these tables, so they are only taken off by running this file.
--
-- WHAT IS LOST: every row in the tables below. Take a backup first, or keep the tables.
-- If another package now reads these tables in place of the removed module, do not run this.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/drop/comments.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_comments;

DELETE FROM public.barako_migrations WHERE owner = 'Comments';

-- Undoes migrations/5.0.0/pages-settings.sql, for a rollback to a Pages module before 4.4.0.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every tenant's own page type and fields. Each tenant then reads Modules:Pages, which
-- is all an earlier module reads.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-pages-settings.sql

DROP TABLE IF EXISTS public.mt_doc_pages_settings;

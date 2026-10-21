-- Undoes migrations/5.2.0/report-definitions.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: Every saved report definition.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-report-definitions.sql

DROP TABLE IF EXISTS public.mt_doc_report_definitions;

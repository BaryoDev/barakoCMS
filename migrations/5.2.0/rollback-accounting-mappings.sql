-- Undoes migrations/5.2.0/accounting-mappings.sql, for a rollback to an Accounting module before mappings.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every tenant's account mappings. The journal actions of an earlier module do not read
-- them.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-accounting-mappings.sql

DROP TABLE IF EXISTS public.mt_doc_accounting_mappings;

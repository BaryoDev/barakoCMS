-- Undoes migrations/5.0.0/blocks.sql, for a rollback to a release before 5.0.0.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every tenant's block schema, presets and bindings. A release before 5.0.0 has no
-- blocks field type and reads none of the three. Pages keep their blocks in their own entries.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-blocks.sql

DROP TABLE IF EXISTS public.mt_doc_blockschema;
DROP TABLE IF EXISTS public.mt_doc_blockpreset;
DROP TABLE IF EXISTS public.mt_doc_blockbinding;

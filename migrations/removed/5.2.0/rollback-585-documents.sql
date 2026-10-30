-- Undoes migrations/5.2.0/585-documents.sql, for a rollback to a release before the Documents module.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every document template of every tenant, and the record of which run produced which
-- document. The stored PDFs stay in the Files module; only the link from a run to its file goes, so
-- a RenderDocument retry after a re-upgrade would store a second copy.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-585-documents.sql

DROP TABLE IF EXISTS public.mt_doc_document_renders;
DROP TABLE IF EXISTS public.mt_doc_document_templates;

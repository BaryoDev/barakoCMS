-- Undoes migrations/6.0.0/content-type-created-at-tenant-index.sql, for a rollback to a release before it.
--
-- WHAT IS LOST: nothing. The index only speeds a query up. An older build does not declare it, so a
-- schema check there would list it as extra.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/6.0.0/rollback-content-type-created-at-tenant-index.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP INDEX IF EXISTS public.mt_doc_contents_idx_content_type_created_at_tenant;

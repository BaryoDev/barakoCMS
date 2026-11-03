-- Undoes migrations/5.5.0/idempotency-created-at-index.sql, for a rollback to a release before it.
--
-- WHAT IS LOST: nothing. The index only speeds a query up.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-idempotency-created-at-index.sql
--
-- Safe to run twice.
-- barako:rerunnable

DROP INDEX IF EXISTS public.mt_doc_idempotency_records_idx_created_at;

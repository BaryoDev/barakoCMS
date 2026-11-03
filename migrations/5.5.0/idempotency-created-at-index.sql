-- Indexes idempotency_records on CreatedAt, for a database that existed before core did.
--
-- The retention sweep counts and deletes records older than a cutoff, ordered by CreatedAt, and
-- the table only had its unique key index. Core declares the index, and Marten creates it only
-- where the table does not yet exist: the app runs AutoCreate.CreateOnly, which adds a missing
-- object and never alters one that is there. Without this file an upgraded database still
-- answers, but every sweep scans the table, and db-assert reports the index as missing.
--
-- Run it with the API stopped, like every upgrade file. A plain build, not CONCURRENTLY, for the
-- reason migrations/4.5.0/refresh-token-hash-index.sql gives.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/idempotency-created-at-index.sql
--
-- Safe to run twice. rollback-idempotency-created-at-index.sql drops the index.
-- barako:rerunnable

CREATE INDEX IF NOT EXISTS mt_doc_idempotency_records_idx_created_at
    ON public.mt_doc_idempotency_records USING btree ((public.mt_immutable_timestamp(data ->> 'CreatedAt')));

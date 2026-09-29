-- Undoes migrations/4.5.0/refresh-token-hash-index.sql, for a rollback to a release before 4.5.0.
-- An older build does not declare the index, so db-assert there would list it as extra.
--
-- A release before 4.5.0 looks refresh tokens up by their plain value, which rows written by 4.5.0
-- do not store. Anyone who signed in or refreshed on 4.5.0 signs in again after the rollback.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.5.0/rollback-refresh-token-hash-index.sql
--
-- Safe to run twice.

DROP INDEX IF EXISTS public.mt_doc_refresh_tokens_uidx_token_hash;

-- Index refresh_tokens.TokenHash on a database that already existed before refresh tokens were
-- stored hashed.
--
-- A presented refresh token is looked up by its SHA-256 hash. Core declares a unique index on the
-- hash, and Marten creates it only where refresh_tokens does not yet exist: the app runs
-- AutoCreate.CreateOnly, which adds a missing object and never alters one that is there. Without
-- this file an upgraded database still works, but every refresh scans the table, and db-assert
-- reports the index as missing.
--
-- Every row written before the upgrade has no TokenHash, so the unique index cannot fail on them.
--
-- CONCURRENTLY so it does not lock sign-in and refresh while it builds. That means it cannot run
-- inside a transaction: run this file on its own, not wrapped in BEGIN.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f migrations/4.5.0/refresh-token-hash-index.sql
--
-- Safe to run twice.
--
-- A CONCURRENTLY build that fails or is cancelled leaves an INVALID index behind under the same
-- name, and IF NOT EXISTS would then skip it and report success. The block below refuses instead.
-- To recover, drop the invalid index and run this file again:
--
--   psql "$DATABASE_URL" -c 'DROP INDEX CONCURRENTLY IF EXISTS public.mt_doc_refresh_tokens_uidx_token_hash;'

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_index x
        JOIN pg_class c ON c.oid = x.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relname = 'mt_doc_refresh_tokens_uidx_token_hash'
          AND NOT x.indisvalid
    ) THEN
        RAISE EXCEPTION 'mt_doc_refresh_tokens_uidx_token_hash exists but is INVALID, left by a failed or cancelled build. Run: DROP INDEX CONCURRENTLY IF EXISTS public.mt_doc_refresh_tokens_uidx_token_hash; then run this file again.';
    END IF;
END
$$;

-- The expression is Marten's, verbatim. RefreshTokenHashIndexMigrationTests compares this file to
-- the index Marten builds.
CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS mt_doc_refresh_tokens_uidx_token_hash
    ON public.mt_doc_refresh_tokens USING btree (((data ->> 'TokenHash'::text)));

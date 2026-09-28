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

-- The expression is Marten's, verbatim. RefreshTokenHashIndexMigrationTests compares this file to
-- the index Marten builds.
CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS mt_doc_refresh_tokens_uidx_token_hash
    ON public.mt_doc_refresh_tokens USING btree (((data ->> 'TokenHash'::text)));

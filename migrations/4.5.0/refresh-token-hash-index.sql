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
-- Run it with the API stopped, like every upgrade file. A plain build, not CONCURRENTLY: a running
-- API keeps a transaction open for as long as it runs, and a CONCURRENTLY build waits for every
-- transaction older than itself, so it never finished against a live API. With the API stopped a
-- plain build of this table takes moments.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.5.0/refresh-token-hash-index.sql
--
-- Safe to run twice. An invalid index left by an earlier CONCURRENTLY attempt under the same name
-- is dropped and built again, since IF NOT EXISTS would otherwise keep it.

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
        DROP INDEX public.mt_doc_refresh_tokens_uidx_token_hash;
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_refresh_tokens_uidx_token_hash
    ON public.mt_doc_refresh_tokens USING btree (((data ->> 'TokenHash'::text)));

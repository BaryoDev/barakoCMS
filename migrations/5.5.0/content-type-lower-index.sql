-- Indexes the lowercased content type on contents, for a database that existed before core did.
--
-- The uniqueness checks filter contents on lower(data ->> 'ContentType'), and the index on the
-- raw value cannot serve that predicate. Core declares the index, and Marten creates it only where
-- the table does not yet exist: the app runs AutoCreate.CreateOnly, which adds a missing object and
-- never alters one that is there. Without this file an upgraded database still answers, but db-assert
-- reports the index as missing.
--
-- Run it with the API stopped, like every upgrade file. A plain build, not CONCURRENTLY, for the
-- reason migrations/4.5.0/refresh-token-hash-index.sql gives. The build reads every content row
-- once, so allow time on a large table.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/content-type-lower-index.sql
--
-- Safe to run twice. rollback-content-type-lower-index.sql drops the index.
-- barako:rerunnable

CREATE INDEX IF NOT EXISTS mt_doc_contents_idx_content_type_lower
    ON public.mt_doc_contents USING btree (lower((data ->> 'ContentType'::text)));

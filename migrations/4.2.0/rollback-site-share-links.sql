-- Undoes migrations/4.2.0/site-share-links.sql, for a rollback to 4.0 or 4.1, which have no share
-- links.
--
-- Those releases do not declare this table, so dropping it leaves the database as they built it.
-- Going back to 3.x needs nothing from this file: migrations/4.0.0/rollback-to-3.x.sql drops the
-- same table, and running both is harmless.
--
-- WHAT IS LOST: every share link. Only the hash of each key is stored, so the links cannot be read
-- out first and put back. After upgrading again, anyone holding an old link gets a 404, and new
-- links have to be created and sent. No content is touched.
--
-- The indexes go with the table, so there is nothing to drop separately.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/rollback-site-share-links.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_site_share_links;

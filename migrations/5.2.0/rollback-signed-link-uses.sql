-- Undoes migrations/5.2.0/signed-link-uses.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: the record of used signed links. A single-use link used before the rollback could be used again.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-signed-link-uses.sql

DROP TABLE IF EXISTS public.mt_doc_signed_link_uses;

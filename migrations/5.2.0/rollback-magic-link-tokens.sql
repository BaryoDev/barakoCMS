-- Undoes migrations/5.2.0/magic-link-tokens.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every pending magic sign-in link. A visitor who asked for one would ask again.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-magic-link-tokens.sql

DROP TABLE IF EXISTS public.mt_doc_magic_link_tokens;

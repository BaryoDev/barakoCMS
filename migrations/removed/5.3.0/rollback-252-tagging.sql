-- Undoes migrations/5.3.0/252-tagging.sql, for a rollback to a release before the Tagging module.
--
-- An earlier release does not declare these tables. Dropping them puts the database back to what that
-- release built. The indexes go with the tables.
--
-- WHAT IS LOST: every vocabulary, term and term assignment of every tenant. Nothing else reads the tables.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/rollback-252-tagging.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_tagging_term_assignments;
DROP TABLE IF EXISTS public.mt_doc_tagging_terms;
DROP TABLE IF EXISTS public.mt_doc_tagging_vocabularies;

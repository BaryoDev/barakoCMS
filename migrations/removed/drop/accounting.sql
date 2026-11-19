-- Drops what BarakoCMS.Accounting created. The package is removed in 6.0.0 and core no longer
-- touches these tables, so they are only taken off by running this file.
--
-- WHAT IS LOST: every row in the tables below. Take a backup first, or keep the tables.
-- Accounts and journal entries written as content entries live in the content tables, not here,
-- and this file does not touch them.
-- If another package now reads these tables in place of the removed module, do not run this.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/drop/accounting.sql
--
-- Safe to run twice.

DROP TABLE IF EXISTS public.mt_doc_accounting_accounts;
DROP TABLE IF EXISTS public.mt_doc_accounting_journal_entries;
DROP TABLE IF EXISTS public.mt_doc_accounting_mappings;
DROP TABLE IF EXISTS public.mt_doc_accounting_number_sequences;

DELETE FROM public.barako_migrations WHERE owner = 'Accounting';

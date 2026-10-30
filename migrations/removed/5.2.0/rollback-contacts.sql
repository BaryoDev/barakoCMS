-- Undoes migrations/removed/5.2.0/contacts.sql, for a rollback to a release before the Contacts module.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every contact and every contact link of every tenant. Nothing else reads the tables.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/rollback-contacts.sql

DROP TABLE IF EXISTS public.mt_doc_contact_links;
DROP TABLE IF EXISTS public.mt_doc_contacts;

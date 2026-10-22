-- Undoes migrations/5.2.0/action-link-uses.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every record of a spent action link. After upgrading again, a link used before the rollback could be used a second time until it expires.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/rollback-action-link-uses.sql

DROP TABLE IF EXISTS public.mt_doc_action_link_uses;

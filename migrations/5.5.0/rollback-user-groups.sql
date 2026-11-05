-- Undoes migrations/5.5.0/user-groups.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: nothing. Releases before 5.5.0 create this table on first use and may already hold
-- user groups in it, so the rollback leaves it in place. An older host reads it as before.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-user-groups.sql

SELECT 1;

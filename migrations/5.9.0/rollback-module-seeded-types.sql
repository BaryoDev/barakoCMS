-- Undoes migrations/5.9.0/module-seeded-types.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: nothing. The table holds what the module seed writes on every start, so it fills
-- again. The rollback leaves it in place, and an older host never reads it.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.9.0/rollback-module-seeded-types.sql

SELECT 1;

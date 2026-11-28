-- Undoes migrations/6.0.0/rollup-sweep-state.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: nothing. The state only says how far today's sweep got. The rollback leaves the
-- table in place, and an older host never reads it.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/6.0.0/rollback-rollup-sweep-state.sql

SELECT 1;

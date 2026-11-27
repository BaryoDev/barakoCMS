-- Undoes migrations/6.0.0/rollup-marks.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: nothing. A mark only says a header needs its rollup values recomputed. The
-- rollback leaves the table in place, and an older host never reads it.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/6.0.0/rollback-rollup-marks.sql

SELECT 1;

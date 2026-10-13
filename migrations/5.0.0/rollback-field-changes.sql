-- Undoes migrations/5.0.0/field-changes.sql, for a rollback to a release before 5.0.0.
--
-- barako:rerunnable
--
-- WHAT IS LOST: the record of every field change. A release before 5.0.0 has no field changes and
-- reads none. A change still running when the API stopped is lost with it; its entries hold
-- whatever the batches that committed wrote, and the type's definition is whatever its last
-- committed step left.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-field-changes.sql

DROP TABLE IF EXISTS public.mt_doc_fieldchange;

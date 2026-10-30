-- Undoes migrations/removed/5.2.0/booking-resources.sql.
--
-- barako:rerunnable
--
-- WHAT IS LOST: every resource, with its time zone, opening hours and exceptions. Bookings are
-- ordinary content entries and stay, pointing at resources that no longer exist.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/rollback-booking-resources.sql

DROP TABLE IF EXISTS public.mt_doc_booking_resources;

-- Writes a Status onto every tenant document stored before tenants had one (#894).
--
-- Until 5.0.0 a tenant was on or off: IsActive. From 5.0.0 it has a Status, Active, Suspended or
-- Archived, stored as its number (0, 1, 2). This file sets Status from IsActive on every tenant
-- document that has no Status: true or absent is Active, false is Suspended. A document that has a
-- Status already is left as it is.
--
-- Data only, and the API does not need it to have run. It reads tenants in memory, where a document
-- with only IsActive comes back with the right Status. The file makes the stored rows say what the
-- API reads, so a query against the table by hand finds Status on every row.
--
-- Its rollback, rollback-tenant-status.sql, changes nothing. IsActive stays in every tenant
-- document, written as a copy of Status (true for Active, false otherwise), so an earlier release
-- reads an Archived or Suspended tenant as inactive and an Active one as active, with or without
-- this file.
--
-- Run it with the API stopped, like every upgrade file:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/tenant-status.sql
--
-- Safe to run twice: a document with a Status is skipped.
-- barako:rerunnable

DO $tenant_status$
DECLARE
    moved integer;
BEGIN
    IF to_regclass('public.mt_doc_tenants') IS NULL THEN
        RAISE NOTICE 'tenant-status: there is no tenants table yet, so there is nothing to move.';
        RETURN;
    END IF;

    UPDATE public.mt_doc_tenants
    SET data = data || jsonb_build_object(
            'Status',
            CASE WHEN data -> 'IsActive' = 'false'::jsonb THEN 1 ELSE 0 END)
    WHERE NOT (data ? 'Status');

    GET DIAGNOSTICS moved = ROW_COUNT;
    RAISE NOTICE 'tenant-status: wrote Status on % tenant document(s).', moved;
END
$tenant_status$;

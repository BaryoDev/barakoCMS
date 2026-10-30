-- The scheduled_entries table, for a database that existed before it did.
--
-- One row per scheduled content change with its due time and status. The schedule sweep fires a due pending row. Global: no tenant_id column. Empty on arrival. Shipped from the Scheduling module, so it runs only where the module is enabled.
--
-- CreateOnly would create it on first boot; this file exists so db-assert passes before the deploy
-- instead of reporting the table as outstanding.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/removed/5.2.0/scheduled-entries.sql
--
-- Safe to run twice. rollback-scheduled-entries.sql drops the table.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_scheduled_entries (
    id                  uuid                        NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_dotnet_type      varchar                     NULL,
    mt_version          bigint                      NOT NULL DEFAULT 0,
    CONSTRAINT pkey_mt_doc_scheduled_entries_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS mt_doc_scheduled_entries_idx_due_at
    ON public.mt_doc_scheduled_entries USING btree ((public.mt_immutable_timestamptz(data ->> 'DueAt')));

CREATE INDEX IF NOT EXISTS mt_doc_scheduled_entries_idx_status
    ON public.mt_doc_scheduled_entries USING btree ((CAST(data ->> 'Status' as integer)));

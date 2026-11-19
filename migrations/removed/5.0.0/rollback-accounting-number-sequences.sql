-- Undoes migrations/5.0.0/accounting-number-sequences.sql, for a rollback to a release before 5.0.0.
--
-- barako:rerunnable
--
-- A release before 5.0.0 numbers journal entries from mt_doc_accounting_number_sequences. Entries
-- posted on 5.0.0 moved the core counters and not those rows, so this copies each year's count back
-- into its row, creating the row where a year started on 5.0.0. A row that is already higher keeps
-- its value. Without it the older release would hand out numbers 5.0.0 already used, and the unique
-- index on the entry number would refuse every post until the year's count caught up.
--
-- WHAT IS LOST: nothing. The counters stay where they are for a later upgrade.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-accounting-number-sequences.sql

DO $rollback$
BEGIN
    IF to_regclass('public.mt_doc_accounting_number_sequences') IS NULL
        OR to_regclass('public.barako_number_counters') IS NULL
    THEN
        RETURN;
    END IF;

    INSERT INTO public.mt_doc_accounting_number_sequences AS s (tenant_id, id, data, mt_dotnet_type)
    SELECT c.tenant_id, 'JE-' || c.period, jsonb_build_object('Id', 'JE-' || c.period, 'Value', c.last_value),
           'BarakoCMS.Accounting.Domain.NumberSequence'
    FROM public.barako_number_counters c
    WHERE c.series = 'accounting.je' AND c.period ~ '^[0-9]{4}$'
    ON CONFLICT (tenant_id, id)
    DO UPDATE SET data = jsonb_set(s.data, '{Value}', to_jsonb(GREATEST((s.data ->> 'Value')::bigint, (EXCLUDED.data ->> 'Value')::bigint))),
                  mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                  mt_last_modified = transaction_timestamp();
END
$rollback$;

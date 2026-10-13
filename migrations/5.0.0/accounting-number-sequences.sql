-- Carries each tenant's journal entry counts from the Accounting module's own rows to the core
-- numbering counters (#584).
--
-- barako:rerunnable
--
-- Up to 4.9 the module numbered entries JE-<year>-<six digits> from one mt_doc_accounting_number_sequences
-- row per tenant and year, under optimistic concurrency. From 5.0 it numbers them with the core
-- gapless allocator, series accounting.je, one counter per tenant and year in
-- barako_number_counters, in the same format. This file copies each row's count into its counter so
-- the next entry continues the year's numbering instead of starting at 1 and clashing with the
-- unique index on the entry number.
--
-- Only rows named JE-<four digits> are read. A counter that is already higher keeps its value, so
-- running this after entries were posted on 5.0 never moves a count back.
--
-- Data only, shipped from the Accounting module, so it runs only where the module is enabled. Run
-- it with the API stopped, as part of the upgrade.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/accounting-number-sequences.sql

DO $migration$
BEGIN
    IF to_regclass('public.mt_doc_accounting_number_sequences') IS NULL
        OR to_regclass('public.barako_number_counters') IS NULL
    THEN
        RAISE NOTICE 'accounting numbers: a table this reads does not exist yet, so there is nothing to carry over';
        RETURN;
    END IF;

    INSERT INTO public.barako_number_counters AS c (tenant_id, series, period, last_value)
    SELECT s.tenant_id, 'accounting.je', substring(s.id FROM 4), (s.data ->> 'Value')::bigint
    FROM public.mt_doc_accounting_number_sequences s
    WHERE s.id ~ '^JE-[0-9]{4}$'
      AND jsonb_typeof(s.data -> 'Value') = 'number'
    ON CONFLICT (tenant_id, series, period)
    DO UPDATE SET last_value = GREATEST(c.last_value, EXCLUDED.last_value);
END
$migration$;

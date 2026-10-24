-- barako:rerunnable
--
-- Gives journal entries the Posted and Voided lifecycle the Accounting module declares from 4.5.0.
--
-- The module seeds a content type definition only when none is stored, so a database that already
-- holds the journalEntry type keeps the one without a lifecycle. This file adds the lifecycle and
-- the VoidReason field to that stored type, and gives each existing entry its state: Voided where
-- Data.Status says Voided, Posted for every other entry. Voided stays in balances and cash flow, where the reversal nets it out.
--
-- Entries and types that already carry a state or a lifecycle are not changed, so running the file
-- twice changes nothing. It is data only: no table, column or index changes.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.3.0/accounting-journal-lifecycle.sql

UPDATE public.mt_doc_contents
SET data = jsonb_set(data, '{LifecycleState}',
        to_jsonb(CASE WHEN data -> 'Data' ->> 'Status' = 'Voided' THEN 'Voided' ELSE 'Posted' END::text)),
    mt_last_modified = transaction_timestamp()
WHERE data ->> 'ContentType' = 'journalEntry'
  AND coalesce(data ->> 'LifecycleState', '') = '';

UPDATE public.mt_doc_contenttypedefinition
SET data = jsonb_set(data, '{Lifecycle}',
        '{"States":["Posted","Voided"],"InitialState":"Posted","Transitions":[{"Name":"Void","From":"Posted","To":"Voided","RequiredFields":["VoidReason"],"OptionalFields":[]}]}'::jsonb),
    mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
    mt_last_modified = transaction_timestamp()
WHERE data ->> 'Name' = 'journalEntry'
  AND coalesce(jsonb_typeof(data -> 'Lifecycle'), 'null') = 'null';

UPDATE public.mt_doc_contenttypedefinition
SET data = jsonb_set(data, '{Fields}',
        (data -> 'Fields') || jsonb_build_array(jsonb_build_object(
            'Name', 'VoidReason', 'DisplayName', '', 'Type', 'string', 'Sensitivity', 0))),
    mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
    mt_last_modified = transaction_timestamp()
WHERE data ->> 'Name' = 'journalEntry'
  AND jsonb_typeof(data -> 'Fields') = 'array'
  AND NOT EXISTS (
      SELECT 1 FROM jsonb_array_elements(data -> 'Fields') f WHERE f ->> 'Name' = 'VoidReason');

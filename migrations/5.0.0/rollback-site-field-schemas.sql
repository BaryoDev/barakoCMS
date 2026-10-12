-- Undoes migrations/5.0.0/site-field-schemas.sql, for a rollback to a release before 5.0.0.
--
-- barako:rerunnable
--
-- A release before 5.0.0 ignores a field's JsonSchema, so leaving it is harmless; it is removed
-- anyway, so a type reads back as it was. Mode goes back to a string field on every site type
-- where it is a choice of exactly Live and Holding, the options the forward file sets. A Mode
-- choice with other options was made by someone, not by the forward file, and is left alone.
--
-- WHAT IS LOST: a schema someone declared on a site json field by hand after the upgrade is
-- removed too, since a field's schema does not say who wrote it. No entry is touched.
--
-- ONE TENANT. Set barako.only_tenant to a handle and only that tenant is looked at, as in the
-- forward file.
--
-- Data only. Run it with the API stopped, and as a superuser with Tenancy:DatabaseEnforcement on.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-site-field-schemas.sql

DO $rollback$
DECLARE
    mode_options constant jsonb := '[{"Value":"Live","Label":"Live"},{"Value":"Holding","Label":"Holding"}]'::jsonb;

    site_type    record;
    field        jsonb;
    fields       jsonb;
    changed      boolean;
    only_tenant  constant text := nullif(btrim(coalesce(current_setting('barako.only_tenant', true), '')), '');
    only_part    constant text := CASE WHEN only_tenant = 'default' THEN '*DEFAULT*' ELSE only_tenant END;
BEGIN
    IF to_regclass('public.mt_doc_contenttypedefinition') IS NULL THEN
        RETURN;
    END IF;

    FOR site_type IN
        SELECT d.id, d.tenant_id, d.data
        FROM public.mt_doc_contenttypedefinition d
        WHERE lower(d.data ->> 'Name') = 'site'
          AND jsonb_typeof(d.data -> 'Fields') = 'array'
          AND (only_part IS NULL OR d.tenant_id = only_part)
        FOR UPDATE
    LOOP
        fields := '[]'::jsonb;
        changed := false;

        FOR field IN SELECT value FROM jsonb_array_elements(site_type.data -> 'Fields')
        LOOP
            IF field ? 'JsonSchema' THEN
                field := field - 'JsonSchema';
                changed := true;
            END IF;

            IF field ->> 'Name' = 'Mode' AND lower(field ->> 'Type') = 'choice' AND field -> 'Options' = mode_options THEN
                field := field || jsonb_build_object('Type', 'string', 'Options', NULL);
                changed := true;
            END IF;

            fields := fields || jsonb_build_array(field);
        END LOOP;

        IF changed THEN
            UPDATE public.mt_doc_contenttypedefinition
            SET data = jsonb_set(data, '{Fields}', fields, true),
                mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                mt_last_modified = transaction_timestamp()
            WHERE id = site_type.id AND tenant_id = site_type.tenant_id;
        END IF;
    END LOOP;
END
$rollback$;

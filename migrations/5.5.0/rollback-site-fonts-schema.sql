-- Undoes migrations/5.5.0/site-fonts-schema.sql, for a rollback to a release before it.
--
-- barako:rerunnable
--
-- A site type's Fonts field goes back to the 5.0.0 schema, where it holds exactly the schema the
-- forward file sets. A schema someone edited since is left alone.
--
-- WHAT IS LOST: nothing in a stored entry. An entry that holds a font object stays as it is, and a
-- later write to Fonts has to give every value as a family name until the forward file runs again.
--
-- ONE TENANT. Set barako.only_tenant to a handle and only that tenant is looked at, as in the
-- forward file.
--
-- Data only. Run it with the API stopped, and as a superuser with Tenancy:DatabaseEnforcement on.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/rollback-site-fonts-schema.sql

DO $rollback$
DECLARE
    old_schema   constant jsonb := '{"additionalProperties":{"type":"string"},"type":"object"}'::jsonb;
    new_schema   constant jsonb := '{"additionalProperties":{"properties":{"family":{"type":"string"},"fallback":{"type":"string"},"url":{"type":"string"}},"required":["family"],"type":["string","object"]},"type":"object"}'::jsonb;

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
            IF field ->> 'Name' = 'Fonts'
                AND lower(field ->> 'Type') = 'json'
                AND field -> 'JsonSchema' = new_schema
            THEN
                field := field || jsonb_build_object('JsonSchema', old_schema);
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

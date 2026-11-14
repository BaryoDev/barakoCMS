-- Lets a site type's Fonts field hold a font object as well as a family name.
--
-- barako:rerunnable
--
-- The site blueprint's Fonts schema used to accept only strings, one family name per key. From
-- 5.6.1 a value may also be an object with a required family and an optional url and fallback,
-- which is the shape a site front end reads when it loads a font file. A site type applied from the
-- blueprint before 5.6.1 holds the old schema, and this file gives it the new one.
--
-- WHICH TYPES. Every content type named site, in every tenant. Within one, a json field named
-- Fonts changes only where its schema is exactly the one 5.0.0 set, so a tenant that edited the
-- schema keeps its own. A field that declares no schema, or another type, is left alone.
-- ONE TENANT. Set barako.only_tenant to a handle and only that tenant is looked at:
--   PGOPTIONS='-c barako.only_tenant=acme' psql "$DATABASE_URL" ... -f this file
--
-- Nothing else on the type changes, and no entry is touched. A stored value keeps reading, and
-- every value the old schema accepted is accepted by the new one.
--
-- Data only: no table, column or index changes, so db-assert answers the same before and after.
-- The type's mt_version is replaced, so an editor holding the type from before reloads it.
--
-- Run it with the API stopped, as part of the upgrade. With Tenancy:DatabaseEnforcement on, run it
-- as a superuser: the tenant policy hides other tenants' rows from the application role.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.6.1/site-fonts-schema.sql
--
-- Safe to run twice: a field that already holds the new schema is not changed again.
-- rollback-site-fonts-schema.sql undoes it.
--
-- The new schema is the blueprint's, in barakoCMS/Blueprints/site.json, and the old one is the
-- Fonts entry of migrations/5.0.0/site-field-schemas.sql. SiteFieldSchemasTests fails when they differ.

DO $migration$
DECLARE
    old_schema   constant jsonb := '{"additionalProperties":{"type":"string"},"type":"object"}'::jsonb;
    new_schema   constant jsonb := '{"additionalProperties":{"properties":{"family":{"type":"string"},"fallback":{"type":"string"},"url":{"type":"string"}},"required":["family"],"type":["string","object"]},"type":"object"}'::jsonb;

    site_type    record;
    field        jsonb;
    fields       jsonb;
    changed      boolean;
    updated      integer := 0;
    only_tenant  constant text := nullif(btrim(coalesce(current_setting('barako.only_tenant', true), '')), '');
    only_part    constant text := CASE WHEN only_tenant = 'default' THEN '*DEFAULT*' ELSE only_tenant END;
BEGIN
    IF to_regclass('public.mt_doc_contenttypedefinition') IS NULL THEN
        RAISE NOTICE 'site fonts schema: a table this reads does not exist yet, so there is nothing to change';
        RETURN;
    END IF;

    FOR site_type IN
        SELECT d.id, d.tenant_id, d.data
        FROM public.mt_doc_contenttypedefinition d
        WHERE lower(d.data ->> 'Name') = 'site'
          AND jsonb_typeof(d.data -> 'Fields') = 'array'
          AND (only_part IS NULL OR d.tenant_id = only_part)
        ORDER BY d.tenant_id, d.id
        FOR UPDATE
    LOOP
        fields := '[]'::jsonb;
        changed := false;

        FOR field IN SELECT value FROM jsonb_array_elements(site_type.data -> 'Fields')
        LOOP
            IF field ->> 'Name' = 'Fonts'
                AND lower(field ->> 'Type') = 'json'
                AND field -> 'JsonSchema' = old_schema
            THEN
                field := field || jsonb_build_object('JsonSchema', new_schema);
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
            updated := updated + 1;
        END IF;
    END LOOP;

    RAISE NOTICE 'site fonts schema: % site type(s) updated', updated;
    IF only_tenant IS NOT NULL THEN
        RAISE NOTICE 'site fonts schema: Only % was looked at', only_tenant;
    END IF;
END
$migration$;

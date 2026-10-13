-- Brings each tenant's site type up to the 5.0.0 site blueprint (#924).
--
-- barako:rerunnable
--
-- From 5.0.0 a json field may declare a JSON Schema that every write to it has to match, and the
-- site blueprint declares one for each of its json fields but Presets. Mode becomes a choice of
-- Live or Holding. A tenant that applied the blueprint before 5.0.0 holds the older definition, and
-- this file gives it the same checks a tenant applying the blueprint today gets.
--
-- WHICH TYPES. Every content type named site, in every tenant. Within one:
--   - a json field named as in the blueprint, that declares no schema yet, gains the blueprint's
--     schema. A field that declares one keeps its own. A field of another type is left alone.
--   - a string field named Mode becomes a choice of Live and Holding, unless an entry of that
--     tenant's site type holds a Mode that is neither. Then Mode is left a string and a NOTICE
--     names the tenant: set the entry's Mode to Live or Holding and run this file again.
-- ONE TENANT. Set barako.only_tenant to a handle and only that tenant is looked at:
--   PGOPTIONS='-c barako.only_tenant=acme' psql "$DATABASE_URL" ... -f this file
--
-- Nothing else on the type changes, and no entry is touched. A stored value that fails its field's
-- new schema keeps reading; the API checks a value only when a write changes it.
--
-- Data only: no table, column or index changes, so db-assert answers the same before and after.
-- The type's mt_version is replaced, so an editor holding the type from before reloads it.
--
-- Run it with the API stopped, as part of the upgrade. With Tenancy:DatabaseEnforcement on, run it
-- as a superuser: the tenant policy hides other tenants' rows from the application role.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/site-field-schemas.sql
--
-- Safe to run twice: a field that already declares a schema, or a Mode that is already a choice,
-- is not changed again. rollback-site-field-schemas.sql undoes it.
--
-- The schemas below are the blueprint's, in barakoCMS/Blueprints/site.json. SiteFieldSchemasTests
-- fails when the two differ.

DO $migration$
DECLARE
    schemas      constant jsonb := '{"Collections":{"additionalProperties":{"properties":{"fields":{"type":"object"},"label":{"type":"string"},"noun":{"items":{"type":"string"},"type":"array"},"pageSize":{"maximum":100,"minimum":1,"type":"integer"},"route":{"type":"string"},"sort":{"type":"string"},"type":{"type":"string"}},"required":["type"],"type":"object"},"type":"object"},"Colors":{"additionalProperties":{"type":"string"},"type":"object"},"Fonts":{"additionalProperties":{"type":"string"},"type":"object"},"FooterColumns":{"items":{"properties":{"heading":{"type":"string"},"links":{"items":{"properties":{"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"}},"required":["label","href"],"type":"object"},"type":"array"}},"type":"object"},"type":"array"},"HeaderActions":{"items":{"properties":{"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"},"variant":{"enum":["primary","secondary","plain"]}},"required":["label","href"],"type":"object"},"maxItems":4,"type":"array"},"HeaderLinks":{"items":{"properties":{"activeOn":{"type":"string"},"children":{"items":{"properties":{"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"}},"required":["label","href"],"type":"object"},"type":"array"},"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"}},"required":["label","href"],"type":"object"},"type":"array"},"Labels":{"additionalProperties":{"type":"string"},"type":"object"},"Layout":{"additionalProperties":{"type":"string"},"type":"object"},"MenuLinks":{"items":{"properties":{"activeOn":{"type":"string"},"children":{"items":{"properties":{"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"}},"required":["label","href"],"type":"object"},"type":"array"},"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"}},"required":["label","href"],"type":"object"},"type":"array"},"OptionColors":{"additionalProperties":{"additionalProperties":{"type":"string"},"type":"object"},"type":"object"},"OptionStyles":{"additionalProperties":{"additionalProperties":{"properties":{"icon":{"type":"string"},"label":{"type":"string"},"tone":{"type":"string"}},"type":"object"},"type":"object"},"type":"object"},"Plugins":{"items":{"type":"string"},"type":"array"},"Radii":{"additionalProperties":{"type":"string"},"type":"object"},"SocialLinks":{"items":{"properties":{"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"network":{"type":"string"}},"required":["network","href"],"type":"object"},"type":"array"},"Space":{"additionalProperties":{"type":["string","number"]},"type":"object"},"StyleRecipes":{"additionalProperties":{"properties":{"class":{"type":"string"},"style":{"additionalProperties":{"type":"string"},"type":"object"}},"type":"object"},"type":"object"},"Text":{"additionalProperties":{"type":["string","number","object"]},"type":"object"},"Tokens":{"additionalProperties":{"type":"string"},"type":"object"},"Tones":{"additionalProperties":{"properties":{"bg":{"type":"string"},"edge":{"type":"string"},"ink":{"type":"string"}},"type":"object"},"type":"object"},"TopBar":{"properties":{"links":{"items":{"properties":{"href":{"pattern":"^(/(?!/)|https?://)","type":"string"},"label":{"type":"string"}},"required":["label","href"],"type":"object"},"type":"array"},"text":{"type":"string"}},"type":"object"},"Variants":{"items":{"properties":{"colors":{"additionalProperties":{"type":"string"},"type":"object"},"label":{"type":"string"},"name":{"type":"string"}},"required":["name"],"type":"object"},"type":"array"}}'::jsonb;
    mode_options constant jsonb := '[{"Value":"Live","Label":"Live"},{"Value":"Holding","Label":"Holding"}]'::jsonb;

    site_type    record;
    field        jsonb;
    fields       jsonb;
    changed      boolean;
    stray_mode   boolean;
    updated      integer := 0;
    only_tenant  constant text := nullif(btrim(coalesce(current_setting('barako.only_tenant', true), '')), '');
    only_part    constant text := CASE WHEN only_tenant = 'default' THEN '*DEFAULT*' ELSE only_tenant END;
BEGIN
    IF to_regclass('public.mt_doc_contenttypedefinition') IS NULL
        OR to_regclass('public.mt_doc_contents') IS NULL
    THEN
        RAISE NOTICE 'site field schemas: a table this reads does not exist yet, so there is nothing to change';
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
        SELECT EXISTS (
            SELECT 1
            FROM public.mt_doc_contents c
            WHERE c.tenant_id = site_type.tenant_id
              AND lower(c.data ->> 'ContentType') = 'site'
              AND jsonb_typeof(c.data -> 'Data' -> 'Mode') IS NOT NULL
              AND jsonb_typeof(c.data -> 'Data' -> 'Mode') <> 'null'
              AND NOT (c.data -> 'Data' -> 'Mode' IN ('"Live"'::jsonb, '"Holding"'::jsonb)))
        INTO stray_mode;

        fields := '[]'::jsonb;
        changed := false;

        FOR field IN SELECT value FROM jsonb_array_elements(site_type.data -> 'Fields')
        LOOP
            IF lower(field ->> 'Type') = 'json'
                AND schemas ? (field ->> 'Name')
                AND coalesce(jsonb_typeof(field -> 'JsonSchema'), 'null') = 'null'
            THEN
                field := field || jsonb_build_object('JsonSchema', schemas -> (field ->> 'Name'));
                changed := true;
            ELSIF field ->> 'Name' = 'Mode' AND lower(field ->> 'Type') = 'string' THEN
                IF stray_mode THEN
                    RAISE NOTICE 'site field schemas: tenant % keeps Mode as a string, an entry holds a Mode that is neither Live nor Holding', site_type.tenant_id;
                ELSE
                    field := field || jsonb_build_object('Type', 'choice', 'Options', mode_options);
                    changed := true;
                END IF;
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

    RAISE NOTICE 'site field schemas: % site type(s) updated', updated;
    IF only_tenant IS NOT NULL THEN
        RAISE NOTICE 'site field schemas: Only % was looked at', only_tenant;
    END IF;
END
$migration$;

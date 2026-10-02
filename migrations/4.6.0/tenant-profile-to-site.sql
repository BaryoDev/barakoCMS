-- Moves each tenant's public profile off the tenant document and into the tenant's site entry (#885).
--
-- barako:rerunnable
--
-- A tenant document carried LogoUrl, About, Location, LocationUrl, SocialHandle, Email and
-- ContactUrl. From 4.6.0 the site entry holds them, as its Logo, About, Location, LocationUrl,
-- SocialHandle, Email and ContactUrl fields, and the tenant API no longer writes them. This file
-- copies each value a tenant document still holds into that tenant's site entry and blanks it on
-- the tenant document. Branding is not moved: it has no fixed shape to map onto a site field.
--
-- Data only. No table, column or index changes, so db-assert answers the same before and after,
-- and the API reads a tenant correctly whether or not this has run: it reads the site entry first
-- and falls back to the tenant document, field by field.
--
-- WHICH TENANTS ARE MOVED. A tenant is moved only when what the API reads is unambiguous:
--   - it has a content type named site, publicly deliverable and not event sourced
--   - that type has exactly one entry that is Published with Public sensitivity
-- Every other tenant is left exactly as it is and named in a NOTICE with the reason: no site type,
-- no published entry, more than one, or an event sourced site type (its stream is the record, and a
-- value written here would be discarded by the next write). Such a tenant keeps answering from the
-- tenant document. Publish its site entry and run this file again to move it.
-- A tenant with no profile values is not touched. The default partition is treated like any other:
-- it is moved if a tenant document with the slug "default" holds values, and no such document
-- exists unless someone stored one by hand.
--
-- WHICH SIDE WINS. The site entry. Where the entry already has a different value in the field, the
-- entry keeps it, the tenant document keeps its own, and a NOTICE names the tenant and the field.
-- Nothing is overwritten and nothing is dropped. From 4.6.0 the API answers with the entry's value
-- for that field, so GET /api/tenants/{handle}/public can answer differently from 4.5 for such a
-- tenant. To settle one, clear the side you do not want and run the file again.
-- A value is also left on the tenant document, with a NOTICE, when moving it would hide or break it:
--   - the site type already has that field and it is not Public, or not a text or url field
--   - the field is a url field and the value is not an http or https address
--
-- WHAT ELSE CHANGES. A site type that lacks one of the fields gains it, optional and Public, but
-- only for a value that is moved, so a tenant with no About gets no About field. The entry's
-- mt_version is replaced, so an editor holding the entry from before gets a 412 on save and reloads
-- instead of overwriting the moved values. The entry's UpdatedAt and its event stream are not
-- touched: the entry's history shows the moved values from its next save on.
--
-- Run it with the API stopped, as part of the upgrade. A release before 4.6.0 reads the profile
-- from the tenant document only, so it would show a moved tenant's profile as empty.
-- With Tenancy:DatabaseEnforcement on, run it as a superuser: the tenant policy hides other
-- tenants' rows from the application role, and the file would then find no site entry to move to.
--
-- One statement, so it commits whole or not at all, with or without --single-transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/tenant-profile-to-site.sql
--
-- Safe to run twice. A value that moved is blank on the tenant document, so the second run skips
-- it. A tenant that was left alone is looked at again, and its NOTICE is printed again.

DO $migration$
DECLARE
    -- tenant document key, site field, display name of the field, type of the field
    mapping constant text[][] := ARRAY[
        ARRAY['LogoUrl',      'Logo',         'Logo',              'url'],
        ARRAY['About',        'About',        'About',             'text'],
        ARRAY['Location',     'Location',     'Location',          'string'],
        ARRAY['LocationUrl',  'LocationUrl',  'Location map link', 'url'],
        ARRAY['SocialHandle', 'SocialHandle', 'Social handle',     'string'],
        ARRAY['Email',        'Email',        'Contact email',     'string'],
        ARRAY['ContactUrl',   'ContactUrl',   'Contact link',      'url']
    ];
    text_types constant text[] := ARRAY['string', 'text', 'markdown', 'richtext'];

    tenant          record;
    slug            text;
    part_id         text;
    found_rows      integer;
    type_id         uuid;
    type_data       jsonb;
    entry_id        uuid;
    entry_data      jsonb;
    fields          jsonb;
    fields_before   jsonb;
    site_data       jsonb;
    tenant_data     jsonb;
    field           jsonb;
    add_field       boolean;
    tenant_key      text;
    site_key        text;
    target_type     text;
    val             text;
    existing_key    text;
    existing_value  jsonb;
    moved_here      integer;
    moved           integer := 0;
    moved_tenants   integer := 0;
    left_tenants    integer := 0;
    left_values     integer := 0;
BEGIN
    IF to_regclass('public.mt_doc_tenants') IS NULL
        OR to_regclass('public.mt_doc_contents') IS NULL
        OR to_regclass('public.mt_doc_contenttypedefinition') IS NULL
    THEN
        RAISE NOTICE 'tenant profile: a table this reads does not exist yet, so there is nothing to move';
        RETURN;
    END IF;

    FOR tenant IN
        SELECT t.id, t.data
        FROM public.mt_doc_tenants t
        WHERE btrim(coalesce(t.data ->> 'LogoUrl', '')) <> ''
           OR btrim(coalesce(t.data ->> 'About', '')) <> ''
           OR btrim(coalesce(t.data ->> 'Location', '')) <> ''
           OR btrim(coalesce(t.data ->> 'LocationUrl', '')) <> ''
           OR btrim(coalesce(t.data ->> 'SocialHandle', '')) <> ''
           OR btrim(coalesce(t.data ->> 'Email', '')) <> ''
           OR btrim(coalesce(t.data ->> 'ContactUrl', '')) <> ''
        ORDER BY t.data ->> 'Slug'
        FOR UPDATE
    LOOP
        slug := tenant.data ->> 'Slug';
        part_id := CASE WHEN slug = 'default' THEN '*DEFAULT*' ELSE slug END;

        SELECT count(*) INTO found_rows
        FROM public.mt_doc_contenttypedefinition d
        WHERE d.tenant_id = part_id AND d.data ->> 'Name' = 'site';

        IF found_rows <> 1 THEN
            RAISE NOTICE 'tenant profile: % left on the tenant document, it has % content type(s) named site', slug, found_rows;
            left_tenants := left_tenants + 1;
            CONTINUE;
        END IF;

        SELECT d.id, d.data INTO type_id, type_data
        FROM public.mt_doc_contenttypedefinition d
        WHERE d.tenant_id = part_id AND d.data ->> 'Name' = 'site'
        FOR UPDATE;

        IF coalesce(type_data ->> 'IsPubliclyDeliverable', 'false') <> 'true' THEN
            RAISE NOTICE 'tenant profile: % left on the tenant document, its site type is not publicly deliverable', slug;
            left_tenants := left_tenants + 1;
            CONTINUE;
        END IF;

        IF to_regclass('public.mt_doc_content_type_sourcing_policies') IS NOT NULL THEN
            SELECT count(*) INTO found_rows
            FROM public.mt_doc_content_type_sourcing_policies p
            WHERE p.tenant_id = part_id AND p.id = 'site' AND p.data ->> 'EventSourced' = 'true';

            IF found_rows > 0 THEN
                RAISE NOTICE 'tenant profile: % left on the tenant document, its site type is event sourced', slug;
                left_tenants := left_tenants + 1;
                CONTINUE;
            END IF;
        END IF;

        SELECT count(*) INTO found_rows
        FROM public.mt_doc_contents c
        WHERE c.tenant_id = part_id
          AND c.data ->> 'ContentType' = 'site'
          AND c.data ->> 'Status' = '1'
          AND coalesce(c.data ->> 'Sensitivity', '0') = '0';

        IF found_rows <> 1 THEN
            RAISE NOTICE 'tenant profile: % left on the tenant document, it has % published site entries', slug, found_rows;
            left_tenants := left_tenants + 1;
            CONTINUE;
        END IF;

        SELECT c.id, c.data INTO entry_id, entry_data
        FROM public.mt_doc_contents c
        WHERE c.tenant_id = part_id
          AND c.data ->> 'ContentType' = 'site'
          AND c.data ->> 'Status' = '1'
          AND coalesce(c.data ->> 'Sensitivity', '0') = '0'
        FOR UPDATE;

        fields := CASE WHEN jsonb_typeof(type_data -> 'Fields') = 'array' THEN type_data -> 'Fields' ELSE '[]'::jsonb END;
        fields_before := fields;
        site_data := CASE WHEN jsonb_typeof(entry_data -> 'Data') = 'object' THEN entry_data -> 'Data' ELSE '{}'::jsonb END;
        tenant_data := tenant.data;
        moved_here := 0;

        FOR i IN 1 .. array_length(mapping, 1) LOOP
            tenant_key := mapping[i][1];
            site_key := mapping[i][2];
            val := tenant.data ->> tenant_key;
            CONTINUE WHEN btrim(coalesce(val, '')) = '';

            SELECT f.value INTO field
            FROM jsonb_array_elements(fields) f
            WHERE lower(f.value ->> 'Name') = lower(site_key)
            LIMIT 1;

            IF FOUND THEN
                add_field := false;
                site_key := field ->> 'Name';
                target_type := lower(coalesce(field ->> 'Type', ''));

                IF coalesce(field ->> 'Sensitivity', '0') <> '0' THEN
                    RAISE NOTICE 'tenant profile: % keeps % on the tenant document, the site field % is not Public', slug, tenant_key, site_key;
                    left_values := left_values + 1;
                    CONTINUE;
                END IF;

                IF target_type <> 'url' AND NOT (target_type = ANY (text_types)) THEN
                    RAISE NOTICE 'tenant profile: % keeps % on the tenant document, the site field % is a % field', slug, tenant_key, site_key, target_type;
                    left_values := left_values + 1;
                    CONTINUE;
                END IF;
            ELSE
                add_field := true;
                target_type := mapping[i][4];
            END IF;

            IF target_type = 'url' AND val !~* '^https?://[^[:space:]/]+' THEN
                RAISE NOTICE 'tenant profile: % keeps % on the tenant document, it is not an http or https address and % is a url field', slug, tenant_key, site_key;
                left_values := left_values + 1;
                CONTINUE;
            END IF;

            -- A key is matched to its field without regard to case, as the API matches them.
            SELECT e.key, e.value INTO existing_key, existing_value
            FROM jsonb_each(site_data) e
            WHERE lower(e.key) = lower(site_key)
              AND jsonb_typeof(e.value) <> 'null'
              AND NOT (jsonb_typeof(e.value) = 'string' AND btrim(e.value #>> '{}') = '')
            ORDER BY e.key
            LIMIT 1;

            IF FOUND THEN
                IF jsonb_typeof(existing_value) <> 'string' OR existing_value #>> '{}' <> val THEN
                    RAISE NOTICE 'tenant profile: % keeps % on the tenant document, the site entry already has a different %', slug, tenant_key, existing_key;
                    left_values := left_values + 1;
                    CONTINUE;
                END IF;
            ELSE
                SELECT e.key INTO existing_key
                FROM jsonb_each(site_data) e
                WHERE lower(e.key) = lower(site_key)
                ORDER BY e.key
                LIMIT 1;

                site_data := jsonb_set(site_data, ARRAY[coalesce(existing_key, site_key)], to_jsonb(val), true);
            END IF;

            IF add_field THEN
                fields := fields || jsonb_build_array(jsonb_build_object(
                    'Name', site_key,
                    'DisplayName', mapping[i][3],
                    'Type', target_type,
                    'ReferenceType', NULL,
                    'Options', NULL,
                    'Multiple', false,
                    'Currency', NULL,
                    'Scale', NULL,
                    'IsRequired', false,
                    'DefaultValue', NULL,
                    'ValidationRules', '{}'::jsonb,
                    'Sensitivity', 0,
                    'VisibleToRoles', '[]'::jsonb,
                    'Mask', 0));
            END IF;

            tenant_data := jsonb_set(tenant_data, ARRAY[tenant_key], 'null'::jsonb, true);
            moved_here := moved_here + 1;
        END LOOP;

        IF moved_here > 0 THEN
            IF fields <> fields_before THEN
                UPDATE public.mt_doc_contenttypedefinition
                SET data = jsonb_set(data, '{Fields}', fields, true)
                WHERE id = type_id AND tenant_id = part_id;
            END IF;

            UPDATE public.mt_doc_contents
            SET data = jsonb_set(data, '{Data}', site_data, true),
                mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                mt_last_modified = transaction_timestamp()
            WHERE id = entry_id AND tenant_id = part_id;

            UPDATE public.mt_doc_tenants
            SET data = tenant_data
            WHERE id = tenant.id;

            moved := moved + moved_here;
            moved_tenants := moved_tenants + 1;
        END IF;
    END LOOP;

    RAISE NOTICE 'tenant profile: moved % value(s) for % tenant(s). % tenant(s) and % more value(s) stay on the tenant document, each named above',
        moved, moved_tenants, left_tenants, left_values;
END
$migration$;

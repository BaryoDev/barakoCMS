-- Undoes migrations/4.6.0/tenant-profile-to-site.sql, for a rollback to a release before 4.6.0.
--
-- barako:rerunnable
--
-- A release before 4.6.0 reads a tenant's public profile from the tenant document only. This file
-- puts the profile back there, and it fills blanks only: for each tenant, a profile value that is
-- blank on the tenant document is filled from the tenant's site entry, where the entry has one.
-- It reads the one Published, Public entry of a publicly deliverable site type, only fields that
-- type marks Public, and a link only when it is an http or https address.
--
-- So, for a value the forward file moved, the earlier release answers with what the site entry
-- holds now, an edit made on 4.6.0 included, and a value cleared on the site entry stays cleared.
-- A value still on the tenant document is not overwritten. That covers a tenant the forward file
-- left alone or was never run for, and a field where the two sides differed: the earlier release
-- answers with the tenant document's value, as it did before the upgrade, even where 4.6.0 was
-- answering with a different one from the site entry. An edit made to such a field on 4.6.0 is
-- not carried back.
--
-- ONE TENANT. Set barako.only_tenant to a handle and only that tenant is looked at, as in the
-- forward file.
--
-- WHAT IS LOST: nothing. The values stay in the site entry as well, and the fields the forward file
-- added to a site type stay on it. An earlier release does not read them from there and is not
-- bothered by them. Running the forward file again after upgrading finds both sides equal and
-- blanks the tenant document's copy.
--
-- Data only: no table, column or index changes. Run it with the API stopped. With
-- Tenancy:DatabaseEnforcement on, run it as a superuser, for the reason the forward file gives.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.6.0/rollback-tenant-profile-to-site.sql
--
-- Safe to run twice: it only fills blanks, and the second run finds none it can fill.

DO $rollback$
DECLARE
    -- tenant document key, site field
    mapping constant text[][] := ARRAY[
        ARRAY['LogoUrl',      'Logo'],
        ARRAY['About',        'About'],
        ARRAY['Location',     'Location'],
        ARRAY['LocationUrl',  'LocationUrl'],
        ARRAY['SocialHandle', 'SocialHandle'],
        ARRAY['Email',        'Email'],
        ARRAY['ContactUrl',   'ContactUrl']
    ];

    tenant              record;
    slug                text;
    part_id             text;
    found_rows          integer;
    type_data           jsonb;
    entry_data          jsonb;
    site_data           jsonb;
    tenant_data         jsonb;
    tenant_key          text;
    site_key            text;
    val                 text;
    restored_here       integer;
    restored            integer := 0;
    restored_tenants    integer := 0;
    only_tenant         constant text := nullif(btrim(coalesce(current_setting('barako.only_tenant', true), '')), '');
BEGIN
    IF to_regclass('public.mt_doc_tenants') IS NULL
        OR to_regclass('public.mt_doc_contents') IS NULL
        OR to_regclass('public.mt_doc_contenttypedefinition') IS NULL
    THEN
        RAISE NOTICE 'tenant profile: a table this reads does not exist, so there is nothing to put back';
        RETURN;
    END IF;

    FOR tenant IN
        SELECT t.id, t.data
        FROM public.mt_doc_tenants t
        WHERE only_tenant IS NULL OR t.data ->> 'Slug' = only_tenant
        ORDER BY t.data ->> 'Slug'
        FOR UPDATE
    LOOP
        slug := tenant.data ->> 'Slug';
        part_id := CASE WHEN slug = 'default' THEN '*DEFAULT*' ELSE slug END;

        SELECT count(*) INTO found_rows
        FROM public.mt_doc_contenttypedefinition d
        WHERE d.tenant_id = part_id AND d.data ->> 'Name' = 'site';
        CONTINUE WHEN found_rows <> 1;

        SELECT d.data INTO type_data
        FROM public.mt_doc_contenttypedefinition d
        WHERE d.tenant_id = part_id AND d.data ->> 'Name' = 'site';
        CONTINUE WHEN coalesce(type_data ->> 'IsPubliclyDeliverable', 'false') <> 'true';
        CONTINUE WHEN jsonb_typeof(type_data -> 'Fields') IS DISTINCT FROM 'array';

        SELECT count(*) INTO found_rows
        FROM public.mt_doc_contents c
        WHERE c.tenant_id = part_id
          AND c.data ->> 'ContentType' = 'site'
          AND c.data ->> 'Status' = '1'
          AND coalesce(c.data ->> 'Sensitivity', '0') = '0';
        CONTINUE WHEN found_rows <> 1;

        SELECT c.data INTO entry_data
        FROM public.mt_doc_contents c
        WHERE c.tenant_id = part_id
          AND c.data ->> 'ContentType' = 'site'
          AND c.data ->> 'Status' = '1'
          AND coalesce(c.data ->> 'Sensitivity', '0') = '0';

        site_data := entry_data -> 'Data';
        CONTINUE WHEN jsonb_typeof(site_data) IS DISTINCT FROM 'object';

        tenant_data := tenant.data;
        restored_here := 0;

        FOR i IN 1 .. array_length(mapping, 1) LOOP
            tenant_key := mapping[i][1];
            site_key := mapping[i][2];
            CONTINUE WHEN btrim(coalesce(tenant.data ->> tenant_key, '')) <> '';

            -- Only a field the type marks Public, which is all the API served from the entry.
            CONTINUE WHEN NOT EXISTS (
                SELECT 1
                FROM jsonb_array_elements(type_data -> 'Fields') f
                WHERE lower(f.value ->> 'Name') = lower(site_key)
                  AND coalesce(f.value ->> 'Sensitivity', '0') = '0');

            SELECT e.value #>> '{}' INTO val
            FROM jsonb_each(site_data) e
            WHERE lower(e.key) = lower(site_key)
              AND jsonb_typeof(e.value) = 'string'
              AND btrim(e.value #>> '{}') <> ''
            ORDER BY e.key
            LIMIT 1;
            CONTINUE WHEN NOT FOUND;
            CONTINUE WHEN tenant_key IN ('LogoUrl', 'LocationUrl', 'ContactUrl') AND val !~* '^https?://[^[:space:]/]+';

            tenant_data := jsonb_set(tenant_data, ARRAY[tenant_key], to_jsonb(val), true);
            restored_here := restored_here + 1;
        END LOOP;

        IF restored_here > 0 THEN
            UPDATE public.mt_doc_tenants
            SET data = tenant_data
            WHERE id = tenant.id;

            restored := restored + restored_here;
            restored_tenants := restored_tenants + 1;
        END IF;
    END LOOP;

    RAISE NOTICE 'tenant profile: put % value(s) back on % tenant document(s)%', restored, restored_tenants,
        CASE WHEN only_tenant IS NULL THEN '' ELSE '. Only ' || only_tenant || ' was looked at' END;
END
$rollback$;

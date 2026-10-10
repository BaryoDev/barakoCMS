-- One membership per person per tenant: a unique index on memberships over UserId and TenantSlug.
--
-- From 4.7.0 core declares this index, and Marten creates it only where memberships does not yet
-- exist: the app runs AutoCreate.CreateOnly, which adds a missing object and never alters one that
-- is there. Without this file an upgraded database works as before, two requests adding the same
-- person at once can still store two rows, and db-assert reports the index as missing.
--
-- The index cannot be built while two rows share a user and a tenant, and this file does not pick
-- which one to keep: each can hold different roles, a different status and a different join date.
-- So it refuses, names how many pairs are affected, and changes nothing. To see them:
--
--   select data ->> 'UserId' as user_id, data ->> 'TenantSlug' as tenant, count(*)
--   from public.mt_doc_memberships
--   group by 1, 2 having count(*) > 1;
--
-- For each pair, decide which row holds what the person should have, delete the others by id, and
-- run this file again. Change the kept row's roles afterwards, through the API, if needed.
--
-- Run it with the API stopped, like every upgrade file. A plain build, not CONCURRENTLY, for the
-- reason migrations/4.5.0/refresh-token-hash-index.sql gives: against a live API a CONCURRENTLY
-- build never finishes. memberships has one row per person per tenant, so the build takes moments.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.7.0/membership-unique-user-tenant.sql
--
-- Safe to run twice. An invalid index left under the same name is dropped and built again, since
-- IF NOT EXISTS would otherwise keep it. The expression is Marten's, verbatim, cast to uuid
-- included, and MembershipUniqueIndexMigrationTests compares it with the index Marten builds.

-- For db-migrate (docs/migrations.md). It records this file without running it when the index is there and valid, or the table is not, in which case the first start creates both.
-- barako:skip-when: select to_regclass('public.mt_doc_memberships') is null or exists (select 1 from pg_index x join pg_class c on c.oid = x.indexrelid join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = 'mt_doc_memberships_uidx_user_id_tenant_slug' and x.indisvalid)

DO $$
DECLARE
    pairs integer;
BEGIN
    SELECT count(*) INTO pairs
    FROM (
        SELECT 1
        FROM public.mt_doc_memberships
        GROUP BY ((data ->> 'UserId'::text))::uuid, (data ->> 'TenantSlug'::text)
        HAVING count(*) > 1
    ) AS duplicated;

    IF pairs > 0 THEN
        RAISE EXCEPTION 'membership-unique-user-tenant: % user and tenant pair(s) have more than one membership row, so the unique index cannot be built. Nothing was changed. The header of this file has the query that lists them and what to do.', pairs;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_index x
        JOIN pg_class c ON c.oid = x.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relname = 'mt_doc_memberships_uidx_user_id_tenant_slug'
          AND NOT x.indisvalid
    ) THEN
        DROP INDEX public.mt_doc_memberships_uidx_user_id_tenant_slug;
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_memberships_uidx_user_id_tenant_slug
    ON public.mt_doc_memberships USING btree ((((data ->> 'UserId'::text))::uuid), ((data ->> 'TenantSlug'::text)));

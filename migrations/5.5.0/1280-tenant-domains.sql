-- The tenant domains table (#1280), for a database that existed before it did.
--
-- One row per domain a tenant has claimed, keyed by the host, so a second claim of the same host
-- fails in the database even when two writes race. Global, like the tenants it points at, so there
-- is no tenant_id column. A row is pending until the tenant proves control of the domain in DNS, and
-- only a verified row routes a request.
--
-- Every domain already in a tenant's Domains list is copied in as verified, so a deployment that
-- served a tenant on its own domain before this release keeps serving it. A host listed by two
-- tenants is skipped: the domain map already routed neither of them, and picking one here would
-- hand the domain to whichever tenant a query returned first. Those two need an operator to decide.
--
-- Padding is trimmed the way .NET trims it. A plain \s does not match a no-break space or the
-- other Unicode spaces in every Postgres locale.
--
-- CreateOnly would create the table on first boot but never fill it, which is why this file exists.
--
-- Plain DDL and inserts, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.5.0/1280-tenant-domains.sql
--
-- Safe to run twice: a domain that already has a row is left as it is.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_tenant_domains (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_tenant_domains_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS mt_doc_tenant_domains_idx_tenant_slug
    ON public.mt_doc_tenant_domains USING btree ((data ->> 'TenantSlug'));

DO $tenant_domains$
DECLARE
    copied  integer;
    skipped integer;
BEGIN
    IF to_regclass('public.mt_doc_tenants') IS NULL THEN
        RAISE NOTICE 'tenant-domains: there is no tenants table yet, so there is nothing to copy.';
        RETURN;
    END IF;

    CREATE TEMPORARY TABLE claimed_domains ON COMMIT DROP AS
    SELECT DISTINCT
        regexp_replace(rtrim(split_part(lower(regexp_replace(d.host, '^[\s\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\s\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$', '', 'g')), ':', 1), '.'), '^www\.', '') AS host,
        t.data ->> 'Slug'                                                                   AS slug
    FROM public.mt_doc_tenants t
    CROSS JOIN LATERAL jsonb_array_elements_text(
        CASE WHEN jsonb_typeof(t.data -> 'Domains') = 'array' THEN t.data -> 'Domains' ELSE '[]'::jsonb END) AS d(host)
    WHERE rtrim(split_part(lower(regexp_replace(d.host, '^[\s\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\s\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$', '', 'g')), ':', 1), '.') <> '';

    SELECT count(*) INTO skipped
    FROM (SELECT host FROM claimed_domains GROUP BY host HAVING count(DISTINCT slug) > 1) shared;

    INSERT INTO public.mt_doc_tenant_domains (id, data)
    SELECT
        c.host,
        jsonb_build_object(
            'Id', c.host,
            'TenantSlug', c.slug,
            'Status', 1,
            'Token', left(replace(gen_random_uuid()::text || gen_random_uuid()::text, '-', ''), 48),
            'CreatedAt', to_char(now() AT TIME ZONE 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"+00:00"'),
            'VerifiedAt', to_char(now() AT TIME ZONE 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"+00:00"'))
    FROM claimed_domains c
    WHERE c.host <> ''
      AND c.host IN (SELECT host FROM claimed_domains GROUP BY host HAVING count(DISTINCT slug) = 1)
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS copied = ROW_COUNT;
    RAISE NOTICE 'tenant-domains: copied % domain(s) as verified, skipped % claimed by more than one tenant.', copied, skipped;
END
$tenant_domains$;

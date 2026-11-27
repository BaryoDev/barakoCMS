-- Index contents for a rollup window on createdAt: type, creation time and tenant.
--
-- A rollup with `where: { createdAt: thisMonth }` counts the entries of its `from` type created in the
-- window. The drain sends one grouped aggregate per 100 marked headers, and each reads the type's
-- entries in the tenant. With this index the aggregate reads only the window's slice of that range.
-- Core declares it, and Marten creates it only where the table does not yet exist: the app runs
-- AutoCreate.CreateOnly, which adds a missing object and never alters one that is there. Without this
-- file an upgraded database still answers, but a createdAt window reads every entry of the type.
--
-- CONCURRENTLY so it does not block writes to mt_doc_contents while a long history is read. That
-- means it cannot run inside a transaction, and it waits for every transaction older than itself, so
-- run it with the API stopped like every upgrade file (docs/migrations.md). If an earlier attempt was
-- cancelled it leaves an invalid index under this name, which IF NOT EXISTS keeps: drop it with
-- rollback-content-type-created-at-tenant-index.sql and run this file again.
--
--   psql "$DATABASE_URL" -f migrations/6.0.0/content-type-created-at-tenant-index.sql
--
-- Safe to run twice. rollback-content-type-created-at-tenant-index.sql drops it.

-- barako:skip-when: select exists (select 1 from pg_index x join pg_class c on c.oid = x.indexrelid join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = 'mt_doc_contents_idx_content_type_created_at_tenant' and x.indisvalid)
-- barako:no-transaction

CREATE INDEX CONCURRENTLY IF NOT EXISTS mt_doc_contents_idx_content_type_created_at_tenant
    ON public.mt_doc_contents USING btree (lower(data ->> 'ContentType'), (public.mt_immutable_timestamp(data ->> 'CreatedAt')), tenant_id);

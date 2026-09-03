-- Index stored_files.ParentFileId on a database that already existed when image variants shipped.
--
-- The module declares this index, and Marten creates it only on a database where stored_files does
-- not yet exist: the app runs AutoCreate.CreateOnly, which adds a missing object and never alters
-- one that is there. So a fresh install has it and every upgraded install does not.
--
-- Nothing in the running code queries by ParentFileId (a variant is loaded by its derived id), so
-- this is not urgent. It becomes necessary the moment something sweeps a parent's variants, which
-- is what a file delete endpoint will have to do.
--
-- CONCURRENTLY so it does not lock writes to stored_files. That means it cannot run inside a
-- transaction: run this file on its own, not wrapped in BEGIN.
--
--   psql "$DATABASE_URL" -f migrations/4.2.0/stored-files-parent-index.sql
--
-- Safe to run twice.

-- The expression is Marten's, verbatim, including the cast to uuid. A plain text expression would
-- be a different index wearing the right name, and every start-up schema assertion would ask to drop
-- and recreate it. StoredFilesIndexMigrationTests compares this file to the index Marten builds.
CREATE INDEX CONCURRENTLY IF NOT EXISTS mt_doc_stored_files_idx_parent_file_id
    ON public.mt_doc_stored_files USING btree ((((data ->> 'ParentFileId'::text))::uuid));

-- Undoes migrations/5.0.0/files-storage-settings.sql: drops the storage settings table, and with it
-- every tenant's saved storage setting. Files already stored in a bucket stay in the bucket, and
-- their records still name it, but nothing can read them until the setting is saved again or the
-- bucket is set in configuration.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.0.0/rollback-files-storage-settings.sql

DROP TABLE IF EXISTS public.mt_doc_storage_settings;

-- The inbound_webhook_events table, for a database that existed before it did (#910).
--
-- One row per provider event the webhook receiver has handled, keyed by "{provider}:{eventId}", so
-- a replay is answered 200 and not processed again. Global: a webhook carries no tenant, so there
-- is no tenant_id column and the id alone is the primary key. Empty on arrival.
--
-- CreateOnly would create it on first boot; this file exists so db-assert passes before the deploy
-- instead of reporting the table as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/910-inbound-webhook-events.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.mt_doc_inbound_webhook_events (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
    CONSTRAINT pkey_mt_doc_inbound_webhook_events_id PRIMARY KEY (id)
);

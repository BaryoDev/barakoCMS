-- Wolverine's message storage (#687 spike): inbox, outbox, dead letters and node tables, in their
-- own schema so the public schema the 3.x upgrade reasons about does not grow tables that are not
-- ours.
--
-- Wolverine creates these itself on startup, following Marten's AutoCreate setting, and a missing
-- table is a creation rather than an alteration, so CreateOnly would build them on first boot. This
-- file exists because db-assert does not know these tables: they are not registered with Marten's
-- schema management, whatever the integration docs say (WolverineOutboxTests pins that), so the
-- deploy gate cannot report them as outstanding and db-patch will not script them. The statements
-- are what Wolverine's own storage scripting emits for this configuration.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.4.0/wolverine-message-storage.sql

CREATE SCHEMA IF NOT EXISTS wolverine;

CREATE TABLE IF NOT EXISTS wolverine.wolverine_outgoing_envelopes (
    id              uuid                        NOT NULL,
    owner_id        integer                     NOT NULL,
    destination     varchar                     NOT NULL,
    deliver_by      timestamp with time zone    NULL,
    body            bytea                       NOT NULL,
    attempts        integer                     NULL DEFAULT 0,
    message_type    varchar                     NOT NULL,
CONSTRAINT pkey_wolverine_outgoing_envelopes_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_outgoing_envelopes_owner ON wolverine.wolverine_outgoing_envelopes USING btree (owner_id) WHERE (owner_id <> 0);

CREATE INDEX idx_wolverine_outgoing_envelopes_recover ON wolverine.wolverine_outgoing_envelopes USING btree (destination) WHERE (owner_id = 0);
CREATE TABLE IF NOT EXISTS wolverine.wolverine_incoming_envelopes (
    id                uuid                        NOT NULL,
    status            varchar                     NOT NULL,
    owner_id          integer                     NOT NULL,
    execution_time    timestamp with time zone    NULL DEFAULT NULL,
    attempts          integer                     NULL DEFAULT 0,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    received_at       varchar                     NULL,
    keep_until        timestamp with time zone    NULL,
CONSTRAINT pkey_wolverine_incoming_envelopes_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_incoming_envelopes_owner ON wolverine.wolverine_incoming_envelopes USING btree (owner_id) WHERE (owner_id <> 0);

CREATE INDEX idx_wolverine_incoming_envelopes_recover ON wolverine.wolverine_incoming_envelopes USING btree (received_at) WHERE (status = 'Incoming' AND owner_id = 0);

CREATE INDEX idx_wolverine_incoming_envelopes_keep_until ON wolverine.wolverine_incoming_envelopes USING btree (keep_until) WHERE (status = 'Handled');
CREATE TABLE IF NOT EXISTS wolverine.wolverine_dead_letters (
    id                   uuid                        NOT NULL,
    execution_time       timestamp with time zone    NULL DEFAULT NULL,
    body                 bytea                       NOT NULL,
    message_type         varchar                     NOT NULL,
    received_at          varchar                     NULL,
    source               varchar                     NULL,
    exception_type       varchar                     NULL,
    exception_message    varchar                     NULL,
    sent_at              timestamp with time zone    NULL,
    replayable           boolean                     NULL,
CONSTRAINT pkey_wolverine_dead_letters_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_dead_letters_replayable ON wolverine.wolverine_dead_letters USING btree (replayable) WHERE (replayable = true);
CREATE TABLE IF NOT EXISTS wolverine.wolverine_nodes (
    id              uuid                        NOT NULL,
    node_number     serial                      NOT NULL,
    description     varchar                     NOT NULL,
    uri             varchar                     NOT NULL,
    started         timestamp with time zone    NOT NULL DEFAULT now(),
    health_check    timestamp with time zone    NOT NULL DEFAULT now(),
    version         varchar                     NULL,
    capabilities    text[]                      NULL,
CONSTRAINT pkey_wolverine_nodes_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS wolverine.wolverine_node_assignments (
    id         varchar                     NOT NULL,
    node_id    uuid                        NULL,
    started    timestamp with time zone    NOT NULL DEFAULT now(),
CONSTRAINT pkey_wolverine_node_assignments_id PRIMARY KEY (id)
);

ALTER TABLE wolverine.wolverine_node_assignments
ADD CONSTRAINT fkey_wolverine_node_assignments_node_id FOREIGN KEY(node_id)
REFERENCES wolverine.wolverine_nodes(id)ON DELETE CASCADE
;

CREATE TABLE IF NOT EXISTS wolverine.wolverine_control_queue (
    id              uuid                        NOT NULL,
    message_type    varchar                     NOT NULL,
    node_id         uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    posted          timestamp with time zone    NOT NULL DEFAULT NOW(),
    expires         timestamp with time zone    NULL,
CONSTRAINT pkey_wolverine_control_queue_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS wolverine.wolverine_node_records (
    id             serial                      NOT NULL,
    node_number    integer                     NOT NULL,
    event_name     varchar                     NOT NULL,
    timestamp      timestamp with time zone    NOT NULL DEFAULT now(),
    description    varchar                     NULL,
CONSTRAINT pkey_wolverine_node_records_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS wolverine.wolverine_agent_restrictions (
    id      uuid       NOT NULL,
    uri     varchar    NOT NULL,
    type    varchar    NOT NULL,
    node    integer    NOT NULL DEFAULT 0,
CONSTRAINT pkey_wolverine_agent_restrictions_id PRIMARY KEY (id)
);

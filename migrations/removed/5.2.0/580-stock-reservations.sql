-- The three stock reservation tables (#580), for a database that existed before reservations did.
--
-- barako_stock_counters holds one row per tenant and counter: the balance and the floor a
-- reservation may not cross. barako_stock_holds holds one row per reservation, with the state it is
-- in (held, confirmed, released, expired) and when it expires. barako_stock_movements is the history:
-- one row per receipt, reservation, confirmation, release and expiry, whose deltas add up to the
-- balance. All three are per tenant and empty on arrival.
--
-- CreateOnly would create them on first boot; this file exists so db-assert passes before the
-- deploy instead of reporting the tables as outstanding.
--
-- Plain DDL, so it runs inside a transaction:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/5.2.0/580-stock-reservations.sql
--
-- Safe to run twice.
-- barako:rerunnable

CREATE TABLE IF NOT EXISTS public.barako_stock_counters (
    tenant_id     varchar    NOT NULL,
    counter       varchar    NOT NULL,
    balance       bigint     NOT NULL,
    floor         bigint     NOT NULL,
    CONSTRAINT pkey_barako_stock_counters_tenant_id_counter PRIMARY KEY (tenant_id, counter)
);

CREATE TABLE IF NOT EXISTS public.barako_stock_holds (
    tenant_id     varchar                     NOT NULL,
    id            uuid                        NOT NULL,
    counter       varchar                     NOT NULL,
    quantity      bigint                      NOT NULL,
    status        varchar                     NOT NULL,
    reference     varchar                     NOT NULL,
    created_at    timestamp with time zone    NOT NULL,
    expires_at    timestamp with time zone    NULL,
    settled_at    timestamp with time zone    NULL,
    CONSTRAINT pkey_barako_stock_holds_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE TABLE IF NOT EXISTS public.barako_stock_movements (
    tenant_id      varchar                     NOT NULL,
    id             uuid                        NOT NULL,
    counter        varchar                     NOT NULL,
    kind           varchar                     NOT NULL,
    delta          bigint                      NOT NULL,
    hold_id        uuid                        NULL,
    reference      varchar                     NOT NULL,
    occurred_at    timestamp with time zone    NOT NULL,
    CONSTRAINT pkey_barako_stock_movements_tenant_id_id PRIMARY KEY (tenant_id, id)
);

CREATE INDEX IF NOT EXISTS idx_barako_stock_holds_open
    ON public.barako_stock_holds (tenant_id, counter)
    WHERE status = 'held';

CREATE INDEX IF NOT EXISTS idx_barako_stock_movements_counter
    ON public.barako_stock_movements (tenant_id, counter, occurred_at);

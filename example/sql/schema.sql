-- The example's database. Applied automatically by compose.yaml on first start; sqlc reads it too (sqlc.yaml).

-- ── Live data (Typesafe.PgSignal) ────────────────────────────────────────────────────────────────────────────────
-- From the library (../sql/pgsignal.sql): sends {"table": ..., "new": row, "old": row} on channel "pgsignal" for every
-- committed change to a table that has the trigger below. "table" is the trigger's argument, the C# row type's name.
CREATE FUNCTION pgsignal_notify() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('pgsignal', json_build_object(
        'table', COALESCE(TG_ARGV[0], TG_TABLE_NAME),
        'new', CASE WHEN TG_OP <> 'DELETE' THEN row_to_json(NEW) END,
        'old', CASE WHEN TG_OP <> 'INSERT' THEN row_to_json(OLD) END)::text);
    RETURN NULL;
END;
$$;

-- ── Orders ───────────────────────────────────────────────────────────────────────────────────────────────────────
CREATE TYPE order_status AS ENUM ('pending', 'paid', 'shipped', 'cancelled');

-- The constraints are the validation: a violation reaches the client as an error with the constraint's name.
CREATE TABLE orders (
    id            bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_name text         NOT NULL CONSTRAINT customer_name_required CHECK (length(trim(customer_name)) > 0),
    total_cents   bigint       NOT NULL CONSTRAINT total_must_be_positive CHECK (total_cents > 0),
    status        order_status NOT NULL DEFAULT 'pending',
    created_at    timestamptz  NOT NULL DEFAULT now()
);

-- 'Order' is the name of sqlc's row type, Db.Order: Watch<Db.Order, ...> gets the changes sent under that name.
CREATE TRIGGER orders_pgsignal
AFTER INSERT OR UPDATE OR DELETE ON orders
FOR EACH ROW EXECUTE FUNCTION pgsignal_notify('Order');

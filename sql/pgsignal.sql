-- Typesafe.PgSignal: copy this into your schema (it is plain SQL, so it belongs with your other DDL).
--
-- pgsignal_notify() sends every committed insert, update and delete on the one channel "pgsignal", as
-- {"table": <table name>, "new": <row or null>, "old": <row or null>}. Nothing is sent for a rolled-back transaction.
-- The server reads the rows into the type given to Subscribe<TRow> and runs each subscriber's filter on both, so it
-- can tell a subscriber that a row left its filter.
--
-- NOTIFY payloads must stay under 8000 bytes, and an update carries the row twice: fine for ordinary rows, not for
-- tables with large text or json columns.
CREATE FUNCTION pgsignal_notify() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('pgsignal', json_build_object(
        'table', TG_TABLE_NAME,
        'new', CASE WHEN TG_OP <> 'DELETE' THEN row_to_json(NEW) END,
        'old', CASE WHEN TG_OP <> 'INSERT' THEN row_to_json(OLD) END)::text);
    RETURN NULL;
END;
$$;

-- Then, for each table clients can subscribe to (Postgres has no trigger for all tables at once):
-- CREATE TRIGGER orders_pgsignal AFTER INSERT OR UPDATE OR DELETE ON orders
-- FOR EACH ROW EXECUTE FUNCTION pgsignal_notify();

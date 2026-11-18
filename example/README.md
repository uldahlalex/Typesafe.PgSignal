# Typesafe.PgSignal example

A live order list: database-first (SQL schema, sqlc), SignalR with a typed TypeScript client (TypedSignalR), and
Typesafe.PgSignal for the live part. Everything here is app code; the library is in `../src`.

```
sql/schema.sql        the database, incl. the pgsignal trigger
sql/queries.sql       queries and commands (C# generated with sqlc into server/gen/Db)
server/
  Contracts/IOrderHub.cs   the API: [Hub] interface + the types it uses ([TranspilationSource])
  OrderHub.cs              implements it: subscribe methods (queries) and commands
  Program.cs               SignalR + PgSignal<OrderHub> + CORS
client/
  src/generated/           generated from Contracts/ by `bun run generate`
  src/signal.ts            the connection and the typed hub proxy
  src/Example.tsx          a live list (useLive with Do()) and a form that creates orders
compose.yaml          Postgres on port 5433 with sql/schema.sql applied
```

## Run

```bash
docker compose up -d                  # Postgres with the schema
cd server && dotnet run               # http://localhost:5080
cd client && bun install && bun dev   # http://localhost:3000
```

Open two tabs, or change data with `docker compose exec postgres psql -U postgres`: both lists follow.

## After changing...

| Changed | Run |
|---|---|
| `sql/schema.sql` | `docker compose down -v && docker compose up -d` (recreates the database), then the sqlc line below |
| `sql/*.sql` | `docker run --rm -e SQLCCACHE=/cache -v ~/.cache/sqlc:/cache -v $PWD:/src -w /src sqlc/sqlc generate` (here) |
| `server/Contracts/` | `bun run generate` in `client/` (the tool is in `dotnet-tools.json`; `dotnet tool restore` once) |
| the library's TS (`../src/typesafe-pgsignal`) | `rm -rf node_modules/typesafe-pgsignal && bun install` in `client/` (Bun copies `file:` packages) |

## Making another table live

1. `sql/schema.sql`:
   `CREATE TRIGGER x_pgsignal AFTER INSERT OR UPDATE OR DELETE ON x FOR EACH ROW EXECUTE FUNCTION pgsignal_notify();`
2. `sql/queries.sql`: a list query with `sqlc.embed(x)`; regenerate.
3. `server/Contracts`: a `[TranspilationSource]` DTO and `Task<XDto[]> SubscribeToXs()` on the hub interface;
   implement it in the hub with `signal.Subscribe<Db.X>(Context.ConnectionId, "x", XDto.From)`
   followed by the query.
4. `bun run generate`, then `useLive(connection, hub, "subscribeToXs", () => hub.subscribeToXs())`.

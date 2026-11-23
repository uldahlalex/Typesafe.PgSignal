# Typesafe.PgSignal example

A live order board: database-first (SQL schema, sqlc), SignalR with a typed TypeScript client (TypedSignalR), and
Typesafe.PgSignal for the live part. Everything here is app code; the library is in `../src`.

```
sql/schema.sql        the database, incl. the pgsignal trigger
sql/queries.sql       queries and commands (C# generated with sqlc into server/gen/Db)
server/
  Contracts/IOrderHub.cs   the API: [Hub] interface + the types it uses ([TranspilationSource]), and an empty
                           receiver interface that only the DevTools GUI (/signalr-dev) needs
  OrderHub.cs              implements it: Watch* streams (queries) and commands
  Program.cs               SignalR + PgSignal + CORS
client/
  src/generated/           generated from Contracts/ by `bun run generate`
  src/signal.ts            the connection and the typed hub proxy
  src/Example.tsx          a board: one live column per status (useLive), and a form that creates orders
compose.yaml          Postgres on port 5433 with sql/schema.sql applied
scripts/              pgschema: plan.sh / apply.sh make the database match sql/schema.sql, dump.sh prints it;
                      sqlc.sh generates server/gen/Db from the SQL
```

## Run

```bash
docker compose up -d                  # Postgres with the schema
cd server && dotnet run               # http://localhost:5080
cd client && bun install && bun dev   # http://localhost:3000
```

Open two tabs, or change data with `docker compose exec postgres psql -U postgres`: both boards follow.

## After changing...

| Changed | Run |
|---|---|
| `sql/schema.sql` | `./scripts/plan.sh` to see the SQL, `./scripts/apply.sh` to run it (keeps the data), then `./scripts/sqlc.sh` |
| `sql/queries.sql` | `./scripts/sqlc.sh` (regenerates `server/gen/Db`) |
| `server/Contracts/` | `bun run generate` in `client/` (the tool is in `dotnet-tools.json`; `dotnet tool restore` once) |
| the library's TS (`../src/typesafe-pgsignal`) | `rm -rf node_modules/typesafe-pgsignal && bun install` in `client/` (Bun copies `file:` packages) |

## The schema

`sql/schema.sql` is the only description of the database: there are no migration files. To change the schema, edit
it, and [pgschema](https://www.pgschema.com) works out the `ALTER`s by comparing the file with the database:

```bash
./scripts/plan.sh    # the SQL it would run; changes nothing
./scripts/apply.sh   # shows the same plan, asks, and runs it in a transaction
./scripts/dump.sh    # the schema the database has now, e.g. to spot changes made by hand
```

They run pgschema in Docker against the compose database (set `PGHOST`, `PGPORT`, ... for another). A fresh volume
gets the file directly through `docker-entrypoint-initdb.d`, so both paths end at the same schema. pgschema can't
rename: renaming a column or table in the file means drop and add, so the data in it is lost; do a rename by hand
in psql first, then apply.

## Making another table live

1. `sql/schema.sql`:
   `CREATE TRIGGER x_pgsignal AFTER INSERT OR UPDATE OR DELETE ON x FOR EACH ROW EXECUTE FUNCTION pgsignal_notify('X');`
   (the argument is the name of sqlc's row type, `Db.X`)
2. `sql/queries.sql`: a list query with `sqlc.embed(x)`; `./scripts/sqlc.sh`.
3. `server/Contracts`: a `[TranspilationSource]` DTO and
   `IAsyncEnumerable<LiveChange<XDto>> WatchXs(CancellationToken ct)` on the hub interface; implement it in the hub
   by returning `signal.Watch<Db.X, XDto>(snapshot: <the list query>, map: XDto.From, where: null, ct)`.
4. `bun run generate`, then `useLive(connection, () => hub.watchXs())`.

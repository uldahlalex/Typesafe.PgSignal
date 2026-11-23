# Typesafe.PgSignal

Live, filtered lists from Postgres over SignalR, for database-first apps. It does what
[EntitySignal](https://github.com/dustout/EntitySignal) does for Entity Framework, but without EF: a Postgres trigger
reports every committed change with `NOTIFY`, and one class in your server `LISTEN`s with plain Npgsql, so writes from anywhere show up: your API, psql, another service, another server instance.

```csharp
// Server: a Watch method is a SignalR stream: the rows right now, then every change to them
public IAsyncEnumerable<LiveChange<OrderDto>> WatchOrdersWithStatus(OrderStatus status, CancellationToken ct)
{
    var dbStatus = status.ToDb();
    return signal.Watch<Db.Order, OrderDto>(
        snapshot: async () => (await queries.ListOrdersByStatusAsync(new(dbStatus), ct)).Select(r => r.Order!.Value),
        map: OrderDto.From, where: o => o.Status == dbStatus, ct);
}
```

```tsx
// Client: a list that stays in sync with the database
const paidOrders = useLive(connection, () => orderHub.watchOrdersWithStatus(OrderStatus.Paid));
```

`orderHub` is the typed proxy that [TypedSignalR.Client.TypeScript](https://github.com/nenoNaninu/TypedSignalR.Client.TypeScript)
generates from the hub's C# interface, so the method name, its arguments and the item type are checked by the
compiler.

| Folder | What |
|---|---|
| `src/Typesafe.PgSignal/PgSignal.cs` | The library, server half: one class, `PgSignal`, read it top to bottom |
| `src/typesafe-pgsignal/src/useLive.ts` | The library, client half: one React hook, `useLive`, read it top to bottom |
| `sql/pgsignal.sql` | The trigger function to copy into your schema |
| `example/` | An app that uses it: `server/`, `client/`, `sql/`, see [example/README.md](example/README.md) |

## How it works

```
any write (API, psql, ...) commits
  -> trigger pgsignal_notify: pg_notify('pgsignal', {"table": "Order", "new": row, "old": row})
  -> PgSignal: one connection, LISTEN pgsignal; for each open Watch<Db.Order, ...> stream it runs `where` on the
     new and the old row:
       matches now                 -> { upserted: map(new) }
       matched before, not now     -> { deleted: map(old) }    (deleted, or left the filter)
       neither                     -> nothing is sent
  -> SignalR stream item -> useLive updates the list by id
```

Every call of a Watch method is its own SignalR stream, with its own id: the same method can run any number of times
with different arguments, and each gets only its own changes. The first item is always `{ snapshot: [...] }`.
Stopping the stream on the client (useLive does it on unmount) cancels it on the server, which removes the
subscription.

## Installation

Neither half is on NuGet or npm yet: both come from this repository, as source. The steps below assume an app with a
`server/` (ASP.NET Core) and a `client/` (React) folder, like `example/`.

**1. The source**, as a git submodule (so the app pins a version):

```bash
git submodule add https://github.com/uldahlalex/Typesafe.PgSignal.git lib/Typesafe.PgSignal
```

Whoever clones the app later needs `git clone --recurse-submodules`, or `git submodule update --init` afterwards.

**2. Server: Typesafe.PgSignal** (the C# project; it brings Npgsql and Tapper.Attributes):

```bash
dotnet add server reference lib/Typesafe.PgSignal/src/Typesafe.PgSignal/Typesafe.PgSignal.csproj
```

And the TypeScript generator, which turns the hub interface into the typed client proxy:

```bash
dotnet add server package TypedSignalR.Client.TypeScript.Attributes --version 1.18.0   # [Hub], [Receiver]
dotnet add server package TypedSignalR.Client.TypeScript.Analyzer --version 1.18.0     # flags unsupported hub types
dotnet add server package Tapper.Analyzer --version 1.15.1                             # flags a missing [TranspilationSource]
dotnet new tool-manifest
dotnet tool install TypedSignalR.Client.TypeScript.Generator --version 1.18.0          # the dotnet tsrts command
```

**3. Client: typesafe-pgsignal** (the React hook). In `client/package.json`:

```json
"scripts": {
  "generate": "cd .. && dotnet tsrts --project server/Server.csproj --output client/src/generated --asm"
},
"dependencies": {
  "@microsoft/signalr": "^10.0.0",
  "typesafe-pgsignal": "file:../lib/Typesafe.PgSignal/src/typesafe-pgsignal"
}
```

Then `bun install` (React 18 or later is the other peer dependency). `--asm` matters: it makes the generator also write
`LiveChange<T>`, which lives in the library. Bun copies `file:` packages into `node_modules`, so after updating the
submodule run `rm -rf node_modules/typesafe-pgsignal && bun install` again.

**4. Database:** add the function and a trigger per table to your schema; see Setup below.

## Setup

**Database.** Paste this into your schema file and change the three marked places. The function goes in once,
*before* any trigger that uses it; without it, `CREATE TRIGGER` fails with
`ERROR: function pgsignal_notify() does not exist`.

```sql
-- ── Typesafe.PgSignal (once per database; the same as sql/pgsignal.sql) ─────────────────────────────────────────
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

-- ── Your tables: one trigger per table clients can watch ─────────────────────────────────────────────────────────
CREATE TABLE products (                                     -- (1) your table
    id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,  --     the client matches rows by a numeric id
    name  text   NOT NULL,
    price bigint NOT NULL
);

CREATE TRIGGER products_pgsignal                            -- (2) any trigger name
AFTER INSERT OR UPDATE OR DELETE ON products                -- (1) the same table
FOR EACH ROW EXECUTE FUNCTION pgsignal_notify('Product');   -- (3) the C# row type's name: Watch<Db.Product, ...>
```

(3) must be exactly the name of the type you give `Watch`: `typeof(Db.Product).Name`. sqlc makes the name singular
and PascalCase (`products` → `Product`, `order_lines` → `OrderLine`); check `Models.cs` after generating. If they
differ, the stream sends its snapshot and then no changes, without an error.

Tables in another schema than `public`: write the table with its schema (`ON shop.products`) and keep the function
in `public`, or call it as `public.pgsignal_notify('Product')` if `public` is not on the `search_path`. Check the
generated type name too; sqlc may prefix the schema (`ShopProduct`).

**Server.** Register it and write Watch methods:

```csharp
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSignalR();
builder.Services.AddSingleton<PgSignal>();                                    // the subscriptions
builder.Services.AddHostedService(sp => sp.GetRequiredService<PgSignal>());   // the LISTEN loop
app.MapHub<OrderHub>("/hubs/orders");

public sealed class OrderHub(PgSignal signal, Db.QueriesSql queries) : Hub, IOrderHub
{
    // Watch methods as above; nothing to clean up on disconnect, SignalR cancels the streams
}
```

`Watch<TRow, TItem>` reads the rows from the notification as `TRow` (usually your query generator's row type, e.g.
sqlc's `Db.Order`): snake_case column names, enum labels like `'pending'`. `snapshot` returns `TRow`s too, and `map`
turns each into what the client gets.

Then `bun run generate` in `client/` to get the typed proxy.

**Client.** Create the connection and the generated proxy once:

```ts
export const connection = new HubConnectionBuilder().withUrl(".../hubs/orders").withAutomaticReconnect().build();
export const orderHub = getHubProxyFactory("IOrderHub").createHubProxy(connection);    // generated
connection.start();
```

```tsx
const orders = useLive(connection, () => orderHub.watchAllOrders());
const filtered = useLive(connection, () => orderHub.watchOrdersWithStatus(status), [status]);
```

`useLive(connection, watch, deps)`: `watch` starts the stream, and `deps` are the values it uses, like `useEffect`.
It waits for the connection, starts the stream, and starts it again when it fails (a lost connection, an error on the
server); the new snapshot then replaces the list. Items are matched by `id`.

## Rules worth knowing

- **One channel for everything.** The trigger puts the row type's name in the payload, and the server LISTENs on
  `pgsignal` once, when it connects. So a Watch stream only adds a subscription in memory, and you name a *row type*,
  never a channel. The cost: every server instance receives every change to every watched table and drops what nobody
  subscribed to. Irrelevant unless you have very high write volumes.
- **The trigger's argument must match the type's name.** Nothing checks it: if they differ (a typo, or a renamed
  type), the stream sends its snapshot and then no changes.
- **Subscribe first, query second.** `Watch` adds the subscription before it runs `snapshot`, so a change committed
  while the query runs is not lost: it waits in the stream's queue and is sent after the snapshot.
- **`where` and the query must select the same rows.** One is the rule for "from now on", the other for "right now";
  nothing checks that they agree. `where` is also the place for authorization: read `Context.User` in the hub method
  and use the result in `where` (the lambda runs later, for every change, outside the hub call).
- **Rows travel in the NOTIFY payload,** which must stay under 8000 bytes, and an update carries the row twice. Fine
  for ordinary rows. Keep big text or json columns in a separate table without a trigger (a view doesn't help: Postgres
  has no AFTER triggers on views).
- **Missed changes are reloaded, not replayed.** NOTIFY is not durable: when the server's LISTEN connection drops, it
  reconnects and every open stream sends a fresh snapshot. When a client's SignalR connection drops, its streams end;
  `useLive` starts them again once it is back.
- **Add `Keepalive=30` to the connection string.** The LISTEN connection waits without a timeout; without keepalives
  a silently dropped connection is never noticed.
- **Several server instances** each LISTEN themselves, so every instance sees every change; no SignalR backplane is
  needed for these streams.

## Not done yet

- Tests for the library itself (the example was verified end to end: two streams of the same method with different
  filters, a row leaving one filter and entering the other, stopping a stream, and a fresh snapshot after killing the
  LISTEN connection).
- Whether the DevTools GUI (TypedSignalR.Client.DevTools, `/signalr-dev`) can call streaming methods.
- Timestamps from a notification arrive as local `DateTime`s, from a query as UTC: the same instant, different offset
  in the JSON.

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

## Setup

**Database.** Add the function from `sql/pgsignal.sql` to your schema, and a trigger per table. Its argument is the
name of the table's C# row type (sqlc's `Db.Order` for `orders`), because `Watch<Db.Order, ...>` gets the changes sent
under `typeof(Db.Order).Name`:

```sql
CREATE TRIGGER orders_pgsignal AFTER INSERT OR UPDATE OR DELETE ON orders
FOR EACH ROW EXECUTE FUNCTION pgsignal_notify('Order');
```

**Server.** Reference `src/Typesafe.PgSignal/Typesafe.PgSignal.csproj` (it brings Npgsql). Then:

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

Run the TypeScript generator with `--asm`, so it also writes `LiveChange<T>` from the library:
`dotnet tsrts --project server/Server.csproj --output client/src/generated --asm`.

**Client.** Add the package (`"typesafe-pgsignal": "file:<path>/src/typesafe-pgsignal"`) and `@microsoft/signalr`:

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

Bun copies `file:` packages into `node_modules`, so after changing the library run `bun install` in the app again.

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
  nothing checks that they agree. `where` is also the place for authorization (`Context.User`).
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

- Tests for the library itself (the example was verified end to end: filters, leaving a filter, writes from psql,
  constraint errors, and resync after killing the LISTEN connection).
- Timestamps from a notification arrive as local `DateTime`s, from a query as UTC: the same instant, different offset
  in the JSON.

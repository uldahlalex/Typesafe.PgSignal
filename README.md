# Typesafe.PgSignal

Live, filtered lists from Postgres over SignalR, for database-first apps. It does what
[EntitySignal](https://github.com/dustout/EntitySignal) does for Entity Framework, but without EF: a Postgres trigger
reports every committed change with `NOTIFY`, and one class in your server `LISTEN`s with plain Npgsql, so writes from anywhere show up: your API, psql, another service, another server instance.

```csharp
// Server: a subscribe method subscribes, then returns the rows as they are now
public async Task<OrderDto[]> SubscribeToOrdersWithStatus(OrderStatus status)
{
    var dbStatus = status.ToDb();
    signal.Subscribe<Db.Order>(Context.ConnectionId, "orders", OrderDto.From,
        where: o => o.Status == dbStatus);                                       // changes from now on
    var rows = await queries.ListOrdersByStatusAsync(new(dbStatus));            // the rows right now
    return [.. rows.Select(r => OrderDto.From(r.Order!.Value))];
}
```

```tsx
// Client: a list that stays in sync with the database
const paidOrders = useLive(connection, orderHub, "subscribeToOrdersWithStatus",
  () => orderHub.subscribeToOrdersWithStatus(OrderStatus.Paid));
```

`orderHub` is the typed proxy that [TypedSignalR.Client.TypeScript](https://github.com/nenoNaninu/TypedSignalR.Client.TypeScript)
generates from the hub's C# interface, so the method name, its arguments and the item type are checked by the
compiler.

| Folder | What |
|---|---|
| `src/Typesafe.PgSignal/PgSignal.cs` | The library, server half: one class, `PgSignal<THub>`, read it top to bottom |
| `src/typesafe-pgsignal/src/useLive.ts` | The library, client half: one React hook, `useLive`, read it top to bottom |
| `sql/pgsignal.sql` | The trigger function to copy into your schema |
| `example/` | An app that uses it: `server/`, `client/`, `sql/`, see [example/README.md](example/README.md) |

## How it works

```
any write (API, psql, ...) commits
  -> trigger pgsignal_notify: pg_notify('pgsignal', {"table": "orders", "new": row, "old": row})
  -> PgSignal<THub>: one connection, LISTEN pgsignal; for each subscription to that table it runs `where` on the
     new and the old row:
       matches now                 -> PgSignalUpsert(subscription, map(new))
       matched before, not now     -> PgSignalDelete(subscription, map(old))    (deleted, or left the filter)
       neither                     -> nothing is sent
  -> SignalR -> useLive (connection.on, ignores other subscriptions) updates the list by id
```

A subscription is identified by the name of the hub method that made it (`[CallerMemberName]`); the client matches
changes by the same name. So a hub method is subscribed with one set of arguments per connection at a time.

## Setup

**Database.** Add the function from `sql/pgsignal.sql` to your schema, and a trigger per table:

```sql
CREATE TRIGGER orders_pgsignal AFTER INSERT OR UPDATE OR DELETE ON orders
FOR EACH ROW EXECUTE FUNCTION pgsignal_notify();
```

**Server.** Reference `src/Typesafe.PgSignal/Typesafe.PgSignal.csproj` (it brings Npgsql). Then:

```csharp
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSignalR();
builder.Services.AddSingleton<PgSignal<OrderHub>>();                                    // the subscriptions
builder.Services.AddHostedService(sp => sp.GetRequiredService<PgSignal<OrderHub>>());   // the LISTEN loop
app.MapHub<OrderHub>("/hubs/orders");

public sealed class OrderHub(PgSignal<OrderHub> signal, Db.QueriesSql queries) : Hub, IOrderHub
{
    // subscribe methods as above, plus:
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        signal.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
```

`Subscribe<TRow>` reads the rows from the notification as `TRow` (usually your query generator's row type, e.g.
sqlc's `Db.Order`): snake_case column names, enum labels like `'pending'`.

**Client.** Add the package (`"typesafe-pgsignal": "file:<path>/src/typesafe-pgsignal"`) and `@microsoft/signalr`:

```ts
export const connection = new HubConnectionBuilder().withUrl(".../hubs/orders").withAutomaticReconnect().build();
export const orderHub = getHubProxyFactory("IOrderHub").createHubProxy(connection);    // generated
connection.start();
```

```tsx
const orders = useLive(connection, orderHub, "subscribeToAllOrders", () => orderHub.subscribeToAllOrders());
const filtered = useLive(connection, orderHub, "subscribeToOrdersWithStatus", () => orderHub.subscribeToOrdersWithStatus(status), [status]);
```

`useLive(connection, hub, subscription, load, deps)`: `subscription` is the name of the hub method `load` calls (the
server tags every change with it); it must be a method of `hub`, the generated proxy, so a typo doesn't compile. `load`
subscribes and returns the snapshot, and
`deps` are the values `load` uses, like `useEffect`. It waits for the connection, retries `load` on failure, and
calls it again after a resync or a reconnect. Items are matched by `id`.

Bun copies `file:` packages into `node_modules`, so after changing the library run `bun install` in the app again.

## Rules worth knowing

- **One channel for everything.** The trigger puts the table name in the payload, and the server LISTENs on
  `pgsignal` once, when it connects. So `Subscribe` only stores the subscription, and you name a *table*, never a
  channel. The cost: every server instance receives every change to every watched table and drops what nobody
  subscribed to. Irrelevant unless you have very high write volumes.
- **Subscribe first, query second.** The server is already listening, so a change committed while the query runs
  arrives as a change. It may arrive *before* the snapshot; `useLive` holds such changes back and applies
  them after it.
- **`where` and the query must select the same rows.** One is the rule for "from now on", the other for "right now";
  nothing checks that they agree. `where` is also the place for authorization (`Context.User`).
- **Rows travel in the NOTIFY payload,** which must stay under 8000 bytes, and an update carries the row twice. Fine
  for ordinary rows; for tables with big text or json columns, subscribe to a view without them.
- **Missed changes are reloaded, not replayed.** NOTIFY is not durable: when the server's LISTEN connection drops, it
  reconnects and tells every client to resync (call the subscribe method again). The same happens when a client's
  SignalR connection comes back, because a new connection has no subscriptions yet.
- **Add `Keepalive=30` to the connection string.** The LISTEN connection waits without a timeout; without keepalives
  a silently dropped connection is never noticed.
- **Several server instances** each LISTEN themselves, so every instance sees every change; no SignalR backplane is
  needed for these subscriptions.

## Not done yet

- Tests for the library itself (the example was verified end to end: filters, leaving a filter, writes from psql,
  constraint errors, and resync after killing the LISTEN connection).
- One subscribe method with several argument sets at once on the same connection.
- Unsubscribing before the connection ends: the server keeps sending until disconnect (the client ignores it).
- Timestamps from a notification arrive as local `DateTime`s, from a query as UTC: the same instant, different offset
  in the JSON.

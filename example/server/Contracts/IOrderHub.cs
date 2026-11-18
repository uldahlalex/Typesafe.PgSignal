using Tapper;
using TypedSignalR.Client;

namespace Server.Contracts;

/// <summary>
/// The API the browser sees. `bun run generate` in client/ turns this interface (and the types it uses, marked
/// [TranspilationSource]) into a typed TypeScript proxy: orderHub.subscribeToAllOrders(), orderHub.createOrder(...).
///
/// Queries are Subscribe* methods: they return the rows right now, and from then on the client gets every change to
/// them (useSync on the client keeps the list current). Commands change data and return at most an id; every client,
/// including the caller, sees the result through its subscriptions.
/// </summary>
[Hub]
public interface IOrderHub
{
    // Queries (live)
    Task<OrderDto[]> SubscribeToAllOrders();
    Task<OrderDto[]> SubscribeToOrdersWithStatus(OrderStatus status);

    // Commands
    Task<long> CreateOrder(string customerName, long totalCents);
    Task UpdateOrderStatus(long id, OrderStatus status);
    Task DeleteOrder(long id);
    Task Do();
}

/// <summary>
/// What the server sends to the browser: the three messages of Typesafe.PgSignal (the names are the constants in
/// PgSignal.cs). The library's TypeScript client handles them; this interface only exists so the DevTools GUI
/// (/signalr-dev) can show them. DevTools needs the hub to be Hub<IOrderReceiver> to find it.
/// </summary>
[Receiver]
public interface IOrderReceiver
{
    Task PgSignalUpsert(string subscription, OrderDto item);
    Task PgSignalDelete(string subscription, OrderDto item);
    Task PgSignalResync(string subscription);
}

/// <summary>An order as clients see it. Built from the database row in one place, <see cref="From"/>.</summary>
[TranspilationSource]
public sealed record OrderDto(long Id, string CustomerName, long TotalCents, OrderStatus Status, DateTime CreatedAt)
{
    public static OrderDto From(Db.Order row) =>
        new(row.Id, row.CustomerName, row.TotalCents, row.Status.ToApi(), row.CreatedAt);
}

/// <summary>
/// The API's copy of the order_status enum in sql/schema.sql (sqlc's Db.OrderStatus can't carry the attribute
/// TypeScript generation needs). Same names, so they map by name.
/// </summary>
[TranspilationSource]
public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
    Cancelled,
}

public static class OrderStatusMapping
{
    public static OrderStatus ToApi(this Db.OrderStatus status) =>
        Enum.TryParse<OrderStatus>(status.ToString(), out var s)
            ? s
            : throw new InvalidOperationException($"Database order status {status} has no API OrderStatus");

    public static Db.OrderStatus ToDb(this OrderStatus status) =>
        Enum.TryParse<Db.OrderStatus>(status.ToString(), out var s) && s != Db.OrderStatus.Invalid
            ? s
            : throw new Microsoft.AspNetCore.SignalR.HubException($"Not a valid order status: {status}");
}

using Tapper;
using TypedSignalR.Client;
using Typesafe.PgSignal;

namespace Server.Contracts;

/// <summary>
/// The API the browser sees. `bun run generate` in client/ turns this interface (and the types it uses, marked
/// [TranspilationSource]) into a typed TypeScript proxy: orderHub.watchAllOrders(), orderHub.createOrder(...).
///
/// Queries are Watch* methods: SignalR streams. The first item is the rows right now, then every change to them
/// (useLive on the client keeps a list current). Each call is its own stream, so the same method can run several
/// times with different arguments. The CancellationToken is SignalR's: it fires when the client stops the stream.
///
/// Commands change data and return at most an id; every client, including the caller, sees the result through its
/// streams.
/// </summary>
[Hub]
public interface IOrderHub
{
    // Queries (live)
    IAsyncEnumerable<LiveChange<OrderDto>> WatchAllOrders(CancellationToken ct);
    IAsyncEnumerable<LiveChange<OrderDto>> WatchOrdersWithStatus(OrderStatus status, CancellationToken ct);

    // Commands
    Task<long> CreateOrder(string customerName, long totalCents);
    Task UpdateOrderStatus(long id, OrderStatus status);
    Task DeleteOrder(long id);
}

/// <summary>
/// Empty: the server sends nothing on its own, everything arrives on the Watch* streams. It exists because the DevTools
/// GUI (/signalr-dev) only finds hubs that derive from Hub&lt;T&gt; (its analyzer reports TSRD009 otherwise).
/// </summary>
[Receiver]
public interface IOrderReceiver;

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

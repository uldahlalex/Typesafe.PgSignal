using Db;
using static Db.ChannelExtensions;   // Stringify() for the channel enum
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using Server.Contracts;
using Typesafe.PgSignal;
using OrderStatus = Server.Contracts.OrderStatus;

namespace Server;

// No publishing here: the trigger in sql/schema.sql publishes every committed change on channel order_changes.
public sealed class OrderHub(PgSignal<OrderHub> signal, Db.QueriesSql queries) : Hub<IOrderReceiver>, IOrderHub
{
    private static readonly string OrderChanges = Db.Channel.OrderChanges.Stringify();

    // ── Queries: subscribe first, then return the rows as they are now ──────────────────────────────────────────

    public async Task<OrderDto[]> SubscribeToAllOrders()
    {
        await signal.Subscribe<Db.Order>(Context.ConnectionId, OrderChanges, OrderDto.From);
        var rows = await queries.ListOrdersAsync(Context.ConnectionAborted);
        return [.. rows.Select(r => OrderDto.From(r.Order!.Value))];
    }

    public async Task<OrderDto[]> SubscribeToOrdersWithStatus(OrderStatus status)
    {
        var dbStatus = status.ToDb();
        // `where` decides which changes this caller gets; the query below decides the snapshot. Keep them the same rule.
        await signal.Subscribe<Db.Order>(Context.ConnectionId, OrderChanges, OrderDto.From, where: o => o.Status == dbStatus);
        var rows = await queries.ListOrdersByStatusAsync(new(dbStatus), Context.ConnectionAborted);
        return [.. rows.Select(r => OrderDto.From(r.Order!.Value))];
    }

    public async Task Do()
    {
        await signal.Subscribe<Order>(Context.ConnectionId, nameof(Do), d => d);
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<long> CreateOrder(string customerName, long totalCents)
    {
        try
        {
            var row = await queries.CreateOrderAsync(new(customerName, totalCents), Context.ConnectionAborted);
            return row!.Value.Id;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // The constraints are the validation; HubException messages reach the client.
            throw new HubException(ex.ConstraintName);
        }
    }

    public async Task UpdateOrderStatus(long id, OrderStatus status)
    {
        var updated = await queries.UpdateOrderStatusAsync(new(status.ToDb(), id), Context.ConnectionAborted);
        if (updated == 0) throw new HubException($"Order {id} not found.");
    }

    public async Task DeleteOrder(long id)
    {
        var deleted = await queries.DeleteOrderAsync(new(id), Context.ConnectionAborted);
        if (deleted == 0) throw new HubException($"Order {id} not found.");
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        signal.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

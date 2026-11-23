using Db;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using Server.Contracts;
using Typesafe.PgSignal;
using OrderStatus = Server.Contracts.OrderStatus;

namespace Server;

// No publishing here: the trigger in sql/schema.sql publishes every committed change to orders.
public sealed class OrderHub(PgSignal signal, Db.QueriesSql queries) : Hub<IOrderReceiver>, IOrderHub
{
    // ── Queries: streams of the rows now, then every change ─────────────────────────────────────────────────────

    public IAsyncEnumerable<LiveChange<OrderDto>> WatchAllOrders(CancellationToken ct) =>
        signal.Watch<Db.Order, OrderDto>(
            snapshot: async () => (await queries.ListOrdersAsync(ct)).Select(r => r.Order!.Value),
            map: OrderDto.From, where: null, ct);

    public IAsyncEnumerable<LiveChange<OrderDto>> WatchOrdersWithStatus(OrderStatus status, CancellationToken ct)
    {
        var dbStatus = status.ToDb();
        // `where` decides which changes this stream gets; the query decides the snapshot. Keep them the same rule.
        return signal.Watch<Db.Order, OrderDto>(
            snapshot: async () => (await queries.ListOrdersByStatusAsync(new(dbStatus), ct)).Select(r => r.Order!.Value),
            map: OrderDto.From, where: o => o.Status == dbStatus, ct);
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
}

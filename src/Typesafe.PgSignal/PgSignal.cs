using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Typesafe.PgSignal;

/// <summary>
/// Live, filtered subscriptions to Postgres tables for the clients of one SignalR hub.
///
/// The whole flow, in this file:
/// 1. A hub method calls <see cref="Subscribe{TRow}"/>, which stores the subscription. That's all it does.
/// 2. <see cref="ExecuteAsync"/> keeps one connection open with LISTEN pgsignal. The trigger in sql/pgsignal.sql sends
///    {"table": ..., "new": row, "old": row} on that one channel for every committed change to every watched table,
///    where "table" is the trigger's argument: the name of the row type, e.g. 'Order' for Db.Order.
/// 3. <see cref="Dispatch"/> runs every subscription whose TRow has that name: if the new row matches its filter the client gets
///    an upsert, else if the old row matched the client gets a delete, else nothing.
/// 4. If the connection breaks, it reconnects and tells every client to reload (resync): changes made while nobody
///    listened are lost.
///
/// Register it in Program.cs and call <see cref="RemoveConnection"/> from the hub's OnDisconnectedAsync.
/// </summary>
public sealed class PgSignal<THub>(NpgsqlDataSource db, IHubContext<THub> hub, ILogger<PgSignal<THub>> log)
    : BackgroundService where THub : Hub
{
    /// <summary>The one channel the trigger notifies on.</summary>
    public const string Channel = "pgsignal";

    // The client side of these names is in src/typesafe-pgsignal/src/client.ts.
    public const string UpsertMethod = "PgSignalUpsert";
    public const string DeleteMethod = "PgSignalDelete";
    public const string ResyncMethod = "PgSignalResync";

    // Rows come from row_to_json: snake_case columns, enum labels like 'pending'.
    private static readonly JsonSerializerOptions RowJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private sealed record Subscription(string ConnectionId, string Name, string Table, Func<RowChange, Message?> Decide);
    private sealed record RowChange(string Table, JsonElement? New, JsonElement? Old);
    private sealed record Message(string Method, object Item);

    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>
    /// Sends the calling client every future change to the rows of type <typeparamref name="TRow"/> that match
    /// <paramref name="where"/>, as <paramref name="map"/>(row). Query the snapshot after this call. Replaces this
    /// connection's earlier subscription with the same name.
    /// </summary>
    /// <typeparam name="TRow">The table's row type. Its name must be the trigger's argument:
    /// Subscribe&lt;Db.Order&gt; gets the changes of the table whose trigger is pgsignal_notify('Order').</typeparam>
    /// <param name="name">Leave out: the calling hub method's name. The client matches changes by it.</param>
    public void Subscribe<TRow>(
        string connectionId, Func<TRow, object> map, Func<TRow, bool>? where = null,
        [CallerMemberName] string name = "")
    {
        Message? Decide(RowChange change)
        {
            if (change.New is { } newJson)
            {
                var row = newJson.Deserialize<TRow>(RowJson)!;
                if (where == null || where(row)) return new Message(UpsertMethod, map(row));
            }

            if (change.Old is not { } oldJson) return null;
            var oldRow = oldJson.Deserialize<TRow>(RowJson)!;
            if (where == null || where(oldRow)) return new Message(DeleteMethod, map(oldRow));
            return null;
        }

        var subscription = new Subscription(connectionId, name.ToLowerInvariant(), typeof(TRow).Name, Decide);
        lock (_lock)
        {
            _subscriptions.RemoveAll(s => s.ConnectionId == connectionId && s.Name == subscription.Name);
            _subscriptions.Add(subscription);
        }
    }

    /// <summary>Forgets the connection's subscriptions. Call it from the hub's OnDisconnectedAsync.</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock) _subscriptions.RemoveAll(s => s.ConnectionId == connectionId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenUntilBroken(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "The LISTEN connection failed; reconnecting in 2 seconds");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenUntilBroken(CancellationToken stoppingToken)
    {
        await using var connection = await db.OpenConnectionAsync(stoppingToken);
        var received = new List<string>();
        connection.Notification += (_, e) => received.Add(e.Payload);

        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
            await listen.ExecuteNonQueryAsync(stoppingToken);

        // Whatever changed while we weren't listening (startup, or a broken connection) is lost: everyone reloads.
        await ResyncAll();

        while (true)
        {
            // Returns after one or more notifications; the Notification event above has added them to `received`.
            await connection.WaitAsync(stoppingToken);
            foreach (var payload in received) await Dispatch(payload);
            received.Clear();
        }
    }

    private async Task Dispatch(string payload)
    {
        RowChange change;
        try
        {
            change = JsonSerializer.Deserialize<RowChange>(payload, RowJson)!;
        }
        catch (JsonException ex)
        {
            log.LogError(ex, "Notification is not {{\"table\": ..., \"new\": ..., \"old\": ...}}: {Payload}", payload);
            return;
        }

        Subscription[] subscriptions;
        lock (_lock) subscriptions = [.. _subscriptions.Where(s => s.Table == change.Table)];

        foreach (var subscription in subscriptions)
        {
            try
            {
                var message = subscription.Decide(change);
                if (message == null) continue;
                await hub.Clients.Client(subscription.ConnectionId).SendAsync(message.Method, subscription.Name, message.Item);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Subscription {Name} of connection {ConnectionId} failed on a change: {Payload}",
                    subscription.Name, subscription.ConnectionId, payload);
            }
        }
    }

    private async Task ResyncAll()
    {
        Subscription[] subscriptions;
        lock (_lock) subscriptions = [.. _subscriptions];

        foreach (var subscription in subscriptions)
            await hub.Clients.Client(subscription.ConnectionId).SendAsync(ResyncMethod, subscription.Name);
    }
}

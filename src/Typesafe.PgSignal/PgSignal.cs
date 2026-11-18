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
/// 1. A hub method calls <see cref="Subscribe{TRow}"/>: the subscription is stored, and the table's channel is
///    LISTENed on the one connection this class keeps open.
/// 2. <see cref="ExecuteAsync"/> waits for notifications on that connection. The trigger in sql/pgsignal.sql sends
///    {"new": row, "old": row} for every committed change.
/// 3. <see cref="Dispatch"/> runs every subscription of that channel: if the new row matches its filter the client gets
///    an upsert, else if the old row matched the client gets a delete, else nothing.
/// 4. If the connection breaks, it reconnects, LISTENs again and tells every client to reload (resync).
///
/// Register it in Program.cs and call <see cref="RemoveConnection"/> from the hub's OnDisconnectedAsync.
/// </summary>
public sealed class PgSignal<THub>(NpgsqlDataSource db, IHubContext<THub> hub, ILogger<PgSignal<THub>> log)
    : BackgroundService where THub : Hub
{
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

    private sealed record Subscription(string ConnectionId, string Name, string Channel, Func<RowChange, Message?> Decide);
    private sealed record RowChange(JsonElement? New, JsonElement? Old);
    private sealed record Message(string Method, object Item);

    private readonly Lock _subscriptionsLock = new();
    private readonly List<Subscription> _subscriptions = [];

    // Everything that touches the connection holds this: Npgsql runs one command at a time per connection.
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private NpgsqlConnection? _connection;
    private readonly HashSet<string> _listening = [];
    private readonly List<(string Channel, string Payload)> _received = [];

    /// <summary>
    /// Sends the calling client every future change to the rows of <paramref name="channel"/>'s table that match
    /// <paramref name="where"/>, as <paramref name="map"/>(row). Returns once Postgres listens on the channel, so query
    /// the snapshot after this call. Replaces this connection's earlier subscription with the same name.
    /// </summary>
    /// <param name="name">Leave out: the calling hub method's name. The client matches changes by it.</param>
    public async Task Subscribe<TRow>(
        string connectionId, string channel, Func<TRow, object> map, Func<TRow, bool>? where = null,
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

        var subscription = new Subscription(connectionId, name.ToLowerInvariant(), channel, Decide);
        lock (_subscriptionsLock)
        {
            _subscriptions.RemoveAll(s => s.ConnectionId == connectionId && s.Name == subscription.Name);
            _subscriptions.Add(subscription);
        }

        await _connectionLock.WaitAsync();
        try
        {
            // Not connected right now: ExecuteAsync LISTENs on every channel when it connects, then sends a resync.
            if (_connection == null) return;
            if (_listening.Contains(channel)) return;
            await Listen(channel);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Forgets the connection's subscriptions. Call it from the hub's OnDisconnectedAsync.</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_subscriptionsLock) _subscriptions.RemoveAll(s => s.ConnectionId == connectionId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Connect(stoppingToken);
                await ResyncAll();
                await ListenForever(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // NOTIFY is not durable: changes until we listen again are lost, hence the resync after Connect.
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

    private async Task Connect(CancellationToken stoppingToken)
    {
        await _connectionLock.WaitAsync(stoppingToken);
        try
        {
            if (_connection != null) await _connection.DisposeAsync();
            _connection = null;
            _listening.Clear();

            var connection = await db.OpenConnectionAsync(stoppingToken);
            connection.Notification += (_, e) => _received.Add((e.Channel, e.Payload));
            _connection = connection;

            string[] channels;
            lock (_subscriptionsLock) channels = [.. _subscriptions.Select(s => s.Channel).Distinct()];
            foreach (var channel in channels) await Listen(channel);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ListenForever(CancellationToken stoppingToken)
    {
        while (true)
        {
            (string Channel, string Payload)[] batch;
            await _connectionLock.WaitAsync(stoppingToken);
            try
            {
                // Notifications arrive while waiting, through the Notification event (which fills _received).
                await _connection!.WaitAsync(TimeSpan.FromMilliseconds(100), stoppingToken);
                batch = [.. _received];
                _received.Clear();
            }
            finally
            {
                _connectionLock.Release();
            }

            foreach (var (channel, payload) in batch) await Dispatch(channel, payload);
        }
    }

    // Call while holding _connectionLock. Channel names are identifiers, so they are double-quoted.
    private async Task Listen(string channel)
    {
        await using var command = new NpgsqlCommand($"LISTEN \"{channel.Replace("\"", "\"\"")}\"", _connection);
        await command.ExecuteNonQueryAsync();
        _listening.Add(channel);
    }

    private async Task Dispatch(string channel, string payload)
    {
        RowChange change;
        try
        {
            change = JsonSerializer.Deserialize<RowChange>(payload, RowJson)!;
        }
        catch (JsonException ex)
        {
            log.LogError(ex, "Notification on {Channel} is not {{\"new\": ..., \"old\": ...}}: {Payload}", channel, payload);
            return;
        }

        Subscription[] subscriptions;
        lock (_subscriptionsLock) subscriptions = [.. _subscriptions.Where(s => s.Channel == channel)];

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
        lock (_subscriptionsLock) subscriptions = [.. _subscriptions];

        foreach (var subscription in subscriptions)
            await hub.Clients.Client(subscription.ConnectionId).SendAsync(ResyncMethod, subscription.Name);
    }
}

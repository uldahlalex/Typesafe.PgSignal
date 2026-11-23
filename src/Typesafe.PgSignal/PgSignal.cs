using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tapper;

namespace Typesafe.PgSignal;

/// <summary>
/// One message of a live stream: exactly one of the three is set. The first message is always a snapshot; after that,
/// changes. A snapshot can come again later (after a lost LISTEN connection): it replaces everything the client has.
/// </summary>
[TranspilationSource]
public sealed record LiveChange<T>(T[]? Snapshot, T? Upserted, T? Deleted);

/// <summary>
/// Live, filtered streams of Postgres rows for SignalR hub methods.
///
/// The whole flow, in this file:
/// 1. A hub method returns <see cref="Watch{TRow,TItem}"/>: an IAsyncEnumerable, which SignalR sends to the client as
///    a stream. It registers the subscription, runs the snapshot query, sends the snapshot, then every change.
/// 2. <see cref="ExecuteAsync"/> keeps one connection open with LISTEN pgsignal. The trigger in sql/pgsignal.sql sends
///    {"table": ..., "new": row, "old": row} on that one channel for every committed change to every watched table,
///    where "table" is the trigger's argument: the name of the row type, e.g. 'Order' for Db.Order.
/// 3. <see cref="Dispatch"/> hands the change to every subscription whose TRow has that name: if the new row matches
///    its filter the stream gets an upsert, else if the old row matched it gets a delete, else nothing.
/// 4. If the LISTEN connection breaks, it reconnects and every stream sends a fresh snapshot: changes made while
///    nobody listened are lost.
/// 5. When the client stops the stream (or disconnects), SignalR cancels it and the subscription is removed.
///
/// Register it in Program.cs as a singleton and as a hosted service.
/// </summary>
public sealed class PgSignal(NpgsqlDataSource db, ILogger<PgSignal> log) : BackgroundService
{
    /// <summary>The one channel the trigger notifies on.</summary>
    public const string Channel = "pgsignal";

    // Rows come from row_to_json: snake_case columns, enum labels like 'pending'.
    private static readonly JsonSerializerOptions RowJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // OnChange and OnResync only queue work for the stream (see Watch); they never block the LISTEN loop.
    private sealed record Subscription(string Table, Action<RowChange> OnChange, Action OnResync);
    private sealed record RowChange(string Table, JsonElement? New, JsonElement? Old);

    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>
    /// The rows of type <typeparamref name="TRow"/> that match <paramref name="where"/>, as a stream: first
    /// <paramref name="snapshot"/>, then every change, each row mapped with <paramref name="map"/>. Return it from a
    /// hub method; it ends when the client stops it.
    /// </summary>
    /// <typeparam name="TRow">The table's row type. Its name must be the trigger's argument:
    /// Watch&lt;Db.Order, ...&gt; gets the changes of the table whose trigger is pgsignal_notify('Order').</typeparam>
    /// <param name="snapshot">The rows right now. Must select the same rows as <paramref name="where"/>.</param>
    public async IAsyncEnumerable<LiveChange<TItem>> Watch<TRow, TItem>(
        Func<Task<IEnumerable<TRow>>> snapshot, Func<TRow, TItem> map, Func<TRow, bool>? where,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Changes queue up here while the snapshot query runs, so none is lost between "query" and "listen".
        // null means: the LISTEN connection was lost, send a fresh snapshot.
        var queue = System.Threading.Channels.Channel.CreateUnbounded<LiveChange<TItem>?>();

        void OnChange(RowChange change)
        {
            if (change.New is { } newJson)
            {
                var row = newJson.Deserialize<TRow>(RowJson)!;
                if (where == null || where(row))
                {
                    queue.Writer.TryWrite(new LiveChange<TItem>(null, map(row), default));
                    return;
                }
            }

            if (change.Old is not { } oldJson) return;
            var oldRow = oldJson.Deserialize<TRow>(RowJson)!;
            if (where == null || where(oldRow)) queue.Writer.TryWrite(new LiveChange<TItem>(null, default, map(oldRow)));
        }

        var subscription = new Subscription(typeof(TRow).Name, OnChange, () => queue.Writer.TryWrite(null));
        lock (_lock) _subscriptions.Add(subscription);
        try
        {
            yield return new LiveChange<TItem>([.. (await snapshot()).Select(map)], default, default);

            await foreach (var change in queue.Reader.ReadAllAsync(ct))
            {
                if (change != null)
                {
                    yield return change;
                    continue;
                }

                yield return new LiveChange<TItem>([.. (await snapshot()).Select(map)], default, default);
            }
        }
        finally
        {
            lock (_lock) _subscriptions.Remove(subscription);
        }
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

        // Whatever changed while we weren't listening (startup, or a broken connection) is lost: every stream reloads.
        Subscription[] all;
        lock (_lock) all = [.. _subscriptions];
        foreach (var subscription in all) subscription.OnResync();

        while (true)
        {
            // Returns after one or more notifications; the Notification event above has added them to `received`.
            await connection.WaitAsync(stoppingToken);
            foreach (var payload in received) Dispatch(payload);
            received.Clear();
        }
    }

    private void Dispatch(string payload)
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
                subscription.OnChange(change);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "A {Table} subscription failed on a change: {Payload}", change.Table, payload);
            }
        }
    }
}

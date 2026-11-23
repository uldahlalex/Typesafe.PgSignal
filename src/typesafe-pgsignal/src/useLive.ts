import { useEffect, useState } from "react";
import { HubConnectionState, type HubConnection, type IStreamResult, type ISubscription } from "@microsoft/signalr";

type Item = { id: number };

// The shape of LiveChange<T> in src/Typesafe.PgSignal/PgSignal.cs. The generated type (generated/Typesafe.PgSignal.ts)
// fits it; the server sends null for the two that aren't set.
type LiveChange<T> = { snapshot?: T[] | null; upserted?: T | null; deleted?: T | null };

/**
 * A list kept in sync with the server:
 *
 *   const orders = useLive(connection, () => orderHub.watchAllOrders());
 *   const paid = useLive(connection, () => orderHub.watchOrdersWithStatus(status), [status]);
 *
 * `watch` calls a Watch* hub method: a SignalR stream that sends the rows right now (a snapshot), then every change.
 * Put the values `watch` uses in `deps`, like useEffect. Each call of the hook is its own stream, so the same hub
 * method can be used any number of times with different arguments.
 *
 * Flow: wait until the connection is up, start the stream, apply what it sends. Unmounting (or new deps) stops the
 * stream, which also ends it on the server. If the stream fails (a lost connection, an error on the server) it starts
 * again once the connection is back, and the new snapshot replaces the list.
 */
export function useLive<T extends Item>(
  connection: HubConnection,
  watch: () => IStreamResult<LiveChange<T>>,
  deps: unknown[] = [],
): T[] {
  const [items, setItems] = useState<T[]>([]);

  useEffect(() => {
    let stopped = false;
    let subscription: ISubscription<LiveChange<T>> | undefined;

    async function start() {
      while (!stopped && connection.state !== HubConnectionState.Connected) {
        await new Promise((r) => setTimeout(r, 100));
      }
      if (stopped) return;

      subscription = watch().subscribe({
        next: (change) => setItems((list) => apply(list, change)),
        error: (err) => {
          console.warn("Live stream failed, starting it again in 1 s:", err);
          setTimeout(start, 1000);
        },
        complete: () => {},
      });
    }
    start();

    return () => {
      stopped = true;
      subscription?.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `watch` is a new function every render; `deps` says when it changes
  }, [connection, ...deps]);

  return items;
}

function apply<T extends Item>(list: T[], change: LiveChange<T>): T[] {
  if (change.snapshot) return change.snapshot;

  const deleted = change.deleted;
  if (deleted) return list.filter((x) => x.id !== deleted.id);

  const upserted = change.upserted;
  if (!upserted) return list;
  if (!list.some((x) => x.id === upserted.id)) return [upserted, ...list];
  return list.map((x) => (x.id === upserted.id ? upserted : x));
}

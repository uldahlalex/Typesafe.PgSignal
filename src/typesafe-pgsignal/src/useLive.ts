import { useEffect, useState } from "react";
import { HubConnectionState, type HubConnection } from "@microsoft/signalr";

// Must match the constants in src/Typesafe.PgSignal/PgSignal.cs.
const Upsert = "PgSignalUpsert";
const Delete = "PgSignalDelete";
const Resync = "PgSignalResync";

type Item = { id: number };

/**
 * A list kept in sync with the server:
 *
 *   const orders = useLive(connection, orderHub, "subscribeToAllOrders", () => orderHub.subscribeToAllOrders());
 *   const paid = useLive(connection, orderHub, "subscribeToOrdersWithStatus", () => orderHub.subscribeToOrdersWithStatus(status), [status]);
 *
 * `subscription` is the name of the hub method `load` calls: the server tags every change with it. It must be a method
 * of `hub` (the typed proxy), so a typo doesn't compile; `hub` is only there for that check. `load` subscribes and
 * returns the rows right now (the snapshot). Put the values `load` uses in `deps`, like useEffect.
 *
 * Flow: register the change handlers, then call `load`. Changes that arrive before the snapshot are held and applied
 * after it. On a resync from the server, or a reconnect, `load` runs again.
 */
export function useLive<H extends object, T extends Item>(
  connection: HubConnection,
  hub: H,
  subscription: keyof H & string,
  load: () => Promise<T[]>,
  deps: unknown[] = [],
): T[] {
  const [items, setItems] = useState<T[]>([]);

  useEffect(() => {
    const name = subscription.toLowerCase();
    let stopped = false;
    let loaded = false;
    let held: [string, T][] = []; // changes that arrive before the snapshot

    // Subscribe and get the snapshot: wait until the connection is up, retry on failure.
    async function reload() {
      loaded = false;
      held = [];
      while (!stopped) {
        if (connection.state !== HubConnectionState.Connected) {
          await new Promise((r) => setTimeout(r, 100));
          continue;
        }
        try {
          const snapshot = await load();
          if (stopped) return;
          setItems(held.reduce((list, [method, item]) => apply(list, method, item), snapshot));
          held = [];
          loaded = true;
          return;
        } catch (err) {
          console.warn(`${subscription} failed, retrying in 2 s:`, err);
          await new Promise((r) => setTimeout(r, 2000));
        }
      }
    }

    function onChange(method: string) {
      return (sub: string, item: T) => {
        if (sub !== name) return; // another component's subscription
        if (!loaded) {
          held.push([method, item]);
          return;
        }
        setItems((list) => apply(list, method, item));
      };
    }
    const onUpsert = onChange(Upsert);
    const onDelete = onChange(Delete);
    const onResync = (sub: string) => {
      if (sub === name) reload();
    };

    connection.on(Upsert, onUpsert);
    connection.on(Delete, onDelete);
    connection.on(Resync, onResync);
    // A reconnect is a new connection id: the server forgot our subscription. SignalR has no way to remove an
    // onreconnected callback, so after unmount this one just does nothing.
    connection.onreconnected(() => {
      if (!stopped) reload();
    });
    reload();

    return () => {
      stopped = true;
      connection.off(Upsert, onUpsert);
      connection.off(Delete, onDelete);
      connection.off(Resync, onResync);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `load` is a new function every render; `deps` says when it changes
  }, [connection, subscription, ...deps]);

  return items;
}

function apply<T extends Item>(list: T[], method: string, item: T): T[] {
  if (method === Delete) return list.filter((x) => x.id !== item.id);
  if (!list.some((x) => x.id === item.id)) return [item, ...list];
  return list.map((x) => (x.id === item.id ? item : x));
}

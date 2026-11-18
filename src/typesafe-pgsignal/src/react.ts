import { useEffect, useState } from "react";
import type { Change, PgSignalClient } from "./client";

export type SyncState = "connecting" | "live" | "reconnecting";

/** The names of the hub methods that return a list: the subscribe methods. */
type SyncMethod<H> = { [K in keyof H]: H[K] extends (...args: any[]) => Promise<readonly any[]> ? K : never }[keyof H] & string;
type ArgsOf<F> = F extends (...args: infer A) => any ? A : never;
type ItemOf<F> = F extends (...args: any[]) => Promise<readonly (infer T)[]> ? T : never;

/**
 * A list that stays in sync with the database:
 *
 *   const { items: orders, state } = useSync(signal, orderHub, "subscribeToAllOrders");
 *
 * `hub` is the typed proxy from TypedSignalR.Client.TypeScript, so the method name, its arguments and the item type
 * are checked. Items are matched by `id`; new ones go on top.
 *
 * Flow: listen for changes first, then call the subscribe method (which returns the snapshot). Changes that arrive
 * before the snapshot are held and applied after it. On resync, load the snapshot again.
 */
export function useSync<H extends object, K extends SyncMethod<H>>(
  client: PgSignalClient,
  hub: H,
  method: K,
  ...args: ArgsOf<H[K]>
): { items: ItemOf<H[K]>[]; state: SyncState } {
  type T = ItemOf<H[K]>;
  const [items, setItems] = useState<T[]>([]);
  const [state, setState] = useState<SyncState>("connecting");
  const argsKey = JSON.stringify(args);

  useEffect(() => {
    let stopped = false;
    let loaded = false;
    let held: Change[] = [];
    let attempt = 0;

    async function load() {
      const mine = ++attempt;
      loaded = false;
      held = [];
      try {
        await client.ready;
        const snapshot: T[] = await (hub[method] as any)(...args);
        if (stopped || mine !== attempt) return;
        setItems(held.reduce(apply, snapshot));
        loaded = true;
        held = [];
        setState("live");
      } catch (err) {
        if (stopped || mine !== attempt) return;
        console.warn(`PgSignal: ${method} failed, retrying in 2 s:`, err);
        setState("reconnecting");
        setTimeout(() => {
          if (!stopped && mine === attempt) load();
        }, 2000);
      }
    }

    const unsubscribe = client.subscribe(method, {
      change(c) {
        if (!loaded) {
          held.push(c);
          return;
        }
        setItems((list) => apply(list, c));
      },
      resync() {
        load();
      },
      disconnected() {
        loaded = false;
        setState("reconnecting");
      },
    });
    load();

    return () => {
      stopped = true;
      unsubscribe();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- args are compared by value (argsKey)
  }, [client, hub, method, argsKey]);

  return { items, state };
}

function apply<T>(list: T[], change: Change): T[] {
  const item = change.item as T;
  const id = (item as { id: unknown }).id;
  const rest = list.filter((x) => (x as { id: unknown }).id !== id);
  if (change.kind === "delete") return rest;

  const index = list.findIndex((x) => (x as { id: unknown }).id === id);
  if (index < 0) return [item, ...list];
  return list.map((x, i) => (i === index ? item : x));
}

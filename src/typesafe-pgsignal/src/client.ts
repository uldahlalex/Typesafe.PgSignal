import type { HubConnection } from "@microsoft/signalr";

// The server side of these names is in src/Typesafe.PgSignal/PgSignal.cs.
const UpsertMethod = "PgSignalUpsert";
const DeleteMethod = "PgSignalDelete";
const ResyncMethod = "PgSignalResync";

export type Change = { kind: "upsert" | "delete"; item: unknown };

/** What a live list (useSync) is told about its subscription. */
export type SubscriptionHandler = {
  change(change: Change): void;
  /** Changes may have been missed: load the snapshot again. */
  resync(): void;
  /** The connection dropped; a resync follows when it is back. */
  disconnected(): void;
};

/**
 * Owns one SignalR connection: starts it, keeps it up, and hands the server's messages to the live lists by
 * subscription name (the hub method's name).
 *
 *   const connection = new HubConnectionBuilder().withUrl(".../hubs/orders").withAutomaticReconnect().build();
 *   export const signal = new PgSignalClient(connection);
 */
export class PgSignalClient {
  /** Resolves when the connection is up. Replaced while reconnecting. */
  ready: Promise<void>;
  private handlers = new Map<string, Set<SubscriptionHandler>>();

  constructor(readonly connection: HubConnection) {
    connection.on(UpsertMethod, (name: string, item: unknown) => this.each(name, (h) => h.change({ kind: "upsert", item })));
    connection.on(DeleteMethod, (name: string, item: unknown) => this.each(name, (h) => h.change({ kind: "delete", item })));
    connection.on(ResyncMethod, (name: string) => this.each(name, (h) => h.resync()));

    // A reconnect is a new connection id, and the server forgot the old one's subscriptions: everyone reloads.
    connection.onreconnecting(() => this.all((h) => h.disconnected()));
    connection.onreconnected(() => this.all((h) => h.resync()));
    // Automatic reconnect gave up: start over ourselves.
    connection.onclose(() => {
      this.all((h) => h.disconnected());
      this.ready = this.start().then(() => this.all((h) => h.resync()));
    });

    this.ready = this.start();
  }

  /** Sends the changes of subscription `name` to `handler` until the returned function is called. */
  subscribe(name: string, handler: SubscriptionHandler): () => void {
    const key = name.toLowerCase();
    if (!this.handlers.has(key)) this.handlers.set(key, new Set());
    this.handlers.get(key)!.add(handler);
    return () => this.handlers.get(key)?.delete(handler);
  }

  private async start() {
    while (true) {
      try {
        await this.connection.start();
        return;
      } catch (err) {
        console.warn("PgSignal: could not connect, retrying in 2 s:", err);
        await new Promise((r) => setTimeout(r, 2000));
      }
    }
  }

  private each(name: string, f: (h: SubscriptionHandler) => void) {
    this.handlers.get(name.toLowerCase())?.forEach(f);
  }

  private all(f: (h: SubscriptionHandler) => void) {
    this.handlers.forEach((set) => set.forEach(f));
  }
}

import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { getHubProxyFactory } from "./generated/TypedSignalR.Client";
import type { IOrderReceiver } from "./generated/TypedSignalR.Client/Server.Contracts";

// One SignalR connection to the server's OrderHub (server/Program.cs maps it at /hubs/orders).
export const connection = new HubConnectionBuilder()
  .withUrl("http://localhost:5080/hubs/orders")
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// The typed hub, generated from server/Contracts/IOrderHub.cs by `bun run generate`: every method, argument and
// return type is checked against the C# interface.
export const orderHub = getHubProxyFactory("IOrderHub").createHubProxy(connection);

// Hub methods can only be called once this has resolved.
export const started = connection.start();

// Listen to one receiver method, typed from IOrderReceiver (server/Contracts/IOrderHub.cs). Returns the function that
// stops listening, so it fits straight into useEffect: useEffect(() => on("pgSignalUpsert", (subscription, order) => ...), []).
export function on<K extends keyof IOrderReceiver>(method: K, handler: (...args: Parameters<IOrderReceiver[K]>) => void) {
  connection.on(method, handler as (...args: unknown[]) => void);
  return () => connection.off(method, handler as (...args: unknown[]) => void);
}

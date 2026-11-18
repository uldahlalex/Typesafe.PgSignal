import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { getHubProxyFactory } from "./generated/TypedSignalR.Client";

// One SignalR connection to the server's OrderHub (server/Program.cs maps it at /hubs/orders).
export const connection = new HubConnectionBuilder()
  .withUrl("http://localhost:5080/hubs/orders")
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// The typed hub, generated from server/Contracts/IOrderHub.cs by `bun run generate`: every method, argument and
// return type is checked against the C# interface.
export const orderHub = getHubProxyFactory("IOrderHub").createHubProxy(connection);

// useLive retries until the connection is up, so nothing has to wait for this.
connection.start();

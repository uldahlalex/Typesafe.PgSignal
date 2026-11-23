import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { getHubProxyFactory } from "./generated/TypedSignalR.Client";

// One SignalR connection to the server's OrderHub (server/Program.cs maps it at /hubs/orders).
export const connection = new HubConnectionBuilder()
  .withUrl("http://localhost:5080/hubs/orders")
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// The typed hub, generated from server/Contracts/IOrderHub.cs by `bun run generate`: every method, argument and
// return type is checked against the C# interface. Watch* methods return streams; useLive turns one into a list.
export const orderHub = getHubProxyFactory("IOrderHub").createHubProxy(connection);

// useLive waits for this; other hub methods can only be called once it has resolved.
export const started = connection.start();

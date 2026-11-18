import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { PgSignalClient } from "typesafe-pgsignal";
import { getHubProxyFactory } from "./generated/TypedSignalR.Client";

// One SignalR connection to the server's OrderHub (server/Program.cs maps it at /hubs/orders).
const connection = new HubConnectionBuilder()
  .withUrl("http://localhost:5080/hubs/orders")
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// Keeps the connection up and routes live changes to useSync. Starts the connection.
export const signal = new PgSignalClient(connection);

// The typed hub, generated from server/Contracts/IOrderHub.cs by `bun run generate`: every method, argument and
// return type is checked against the C# interface.
export const orderHub = getHubProxyFactory("IOrderHub").createHubProxy(connection);

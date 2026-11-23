import { useEffect, useState, type FormEvent } from "react";
import type { OrderDto } from "./generated/Server.Contracts";
import { connection, on, orderHub, started } from "./signal";

// The smallest realtime component: subscribe with Do(), show every order change the server sends, create orders.
// Do() returns no snapshot, so the list starts empty and fills with changes made after the page loaded.
export default function Example() {
  const [orders, setOrders] = useState<OrderDto[]>([]);
  const [error, setError] = useState("");

  // What the server sends: a new or changed order, or a deleted one.
  useEffect(() => on("pgSignalUpsert", (_subscription, order) =>
    setOrders((list) => list.some((o) => o.id === order.id)
      ? list.map((o) => (o.id === order.id ? order : o))
      : [order, ...list])), []);
  useEffect(() => on("pgSignalDelete", (_subscription, order) =>
    setOrders((list) => list.filter((o) => o.id !== order.id))), []);

  // Subscribe once connected, and again after a reconnect: the server forgets subscriptions of a lost connection.
  useEffect(() => {
    started.then(() => orderHub.do());
    connection.onreconnected(() => orderHub.do());
  }, []);

  async function onCreate(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    e.currentTarget.reset();
    setError("");
    try {
      await orderHub.createOrder(String(form.get("name")), Math.round(Number(form.get("total")) * 100));
    } catch (err) {
      // A HubException's message comes after SignalR's own prefix.
      setError(String(err).replace(/^.*HubException: /, ""));
    }
  }

  return (
    <main>
      <h1>Orders (live)</h1>

      <form onSubmit={onCreate}>
        <input name="name" placeholder="Customer name" />
        <input name="total" type="number" step="0.01" placeholder="Total (DKK)" />
        <button>Create order</button>
      </form>
      {error && <p className="error">{error}</p>}

      <ul>
        {orders.map((o) => (
          <li key={o.id}>
            #{o.id} {o.customerName}: {(o.totalCents / 100).toFixed(2)} DKK
          </li>
        ))}
      </ul>
    </main>
  );
}

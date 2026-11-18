import { useState, type FormEvent } from "react";
import { useLive } from "typesafe-pgsignal";
import type { OrderDto } from "./generated/Server.Contracts";
import { connection, orderHub } from "./signal";

// The smallest realtime component: subscribe with Do(), show every order change the server sends, create orders.
// Do() returns no snapshot, so the list starts empty and fills with changes made after the page loaded.
export default function Example() {
  // "do" must be a method of orderHub, or this doesn't compile. Do() returns no snapshot, hence the empty list (typed,
  // so the item type is known).
  const orders = useLive(connection, orderHub, "do", async (): Promise<OrderDto[]> => {
    await orderHub.do();
    return [];
  });
  const [error, setError] = useState("");

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

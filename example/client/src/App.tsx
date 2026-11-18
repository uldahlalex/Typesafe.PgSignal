import { useState, type FormEvent } from "react";
import { useSync } from "typesafe-pgsignal/react";
import { OrderStatus, type OrderDto } from "./generated/Server.Contracts";
import { orderHub, signal } from "./signal";
import "./index.css";

const money = (cents: number) =>
  (cents / 100).toLocaleString("da-DK", { style: "currency", currency: "DKK" });

const statusLabel = (s: OrderStatus) => OrderStatus[s].toLowerCase();

const nextStatus: Partial<Record<OrderStatus, OrderStatus>> = {
  [OrderStatus.Pending]: OrderStatus.Paid,
  [OrderStatus.Paid]: OrderStatus.Shipped,
};

export function App() {
  // Live queries: each list stays in sync with the database. Commands below don't update them; their changes arrive
  // through these subscriptions, like everyone else's.
  const { items: orders, state } = useSync(signal, orderHub, "subscribeToAllOrders");
  const [filter, setFilter] = useState(OrderStatus.Paid);
  const { items: filtered } = useSync(signal, orderHub, "subscribeToOrdersWithStatus", filter);

  const [error, setError] = useState<string | null>(null);

  async function run(command: () => Promise<unknown>) {
    setError(null);
    try {
      await command();
    } catch (err) {
      // A violated constraint arrives as its name, e.g. "total_must_be_positive".
      const message = err instanceof Error ? err.message : String(err);
      setError(message.replace(/^.*HubException: /, "").replaceAll("_", " "));
    }
  }

  function onCreate(ev: FormEvent<HTMLFormElement>) {
    ev.preventDefault();
    const form = new FormData(ev.currentTarget);
    const kroner = Number(form.get("total") ?? 0);
    run(() => orderHub.createOrder(String(form.get("customerName") ?? ""), Math.round(kroner * 100)));
    ev.currentTarget.reset();
  }

  return (
    <main>
      <header>
        <h1>Orders</h1>
        <span className={`badge ${state}`}>{state}</span>
      </header>

      <p className="hint">
        Open this page in two tabs, or change an order with psql: every change shows up everywhere. The second list
        only gets orders with the chosen status, so an order leaves it when its status changes.
      </p>

      <form onSubmit={onCreate}>
        <input name="customerName" placeholder="Customer name" />
        <input name="total" type="number" step="0.01" placeholder="Total (DKK)" />
        <button type="submit">Create order</button>
      </form>

      {error && <p className="error">{error}</p>}

      <h2>All orders</h2>
      <OrderTable orders={orders} run={run} />

      <h2>
        Orders that are{" "}
        <select value={filter} onChange={(e) => setFilter(Number(e.target.value) as OrderStatus)}>
          {[OrderStatus.Pending, OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Cancelled].map((s) => (
            <option key={s} value={s}>
              {statusLabel(s)}
            </option>
          ))}
        </select>
      </h2>
      <OrderTable orders={filtered} run={run} />
    </main>
  );
}

function OrderTable({ orders, run }: { orders: OrderDto[]; run: (command: () => Promise<unknown>) => void }) {
  return (
    <table>
      <thead>
        <tr>
          <th>#</th>
          <th>Customer</th>
          <th className="num">Total</th>
          <th>Status</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {orders.map((o) => {
          const next = nextStatus[o.status];
          return (
            <tr key={o.id}>
              <td>{o.id}</td>
              <td>{o.customerName}</td>
              <td className="num">{money(o.totalCents)}</td>
              <td>
                <span className={`status ${statusLabel(o.status)}`}>{statusLabel(o.status)}</span>
              </td>
              <td className="actions">
                {next !== undefined && (
                  <button onClick={() => run(() => orderHub.updateOrderStatus(o.id, next))}>
                    Mark {statusLabel(next)}
                  </button>
                )}
                <button onClick={() => run(() => orderHub.deleteOrder(o.id))}>Delete</button>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

export default App;

import { useState, type FormEvent } from "react";
import { useLive } from "typesafe-pgsignal";
import { OrderStatus, type OrderDto } from "./generated/Server.Contracts";
import { connection, orderHub } from "./signal";

// A board of live columns. Every column calls the same hub method, watchOrdersWithStatus, with its own status: each
// call is its own stream, so when an order moves from Pending to Paid, the server sends a delete on the Pending
// stream and an upsert on the Paid stream. Open two tabs, or change data in psql: both boards follow.
const columns = [OrderStatus.Pending, OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Cancelled];

export default function Example() {
  const [error, setError] = useState("");

  // Commands return nothing useful to show: their result arrives on the streams, like everyone else's changes.
  async function run(command: () => Promise<unknown>) {
    setError("");
    try {
      await command();
    } catch (err) {
      // A HubException's message comes after SignalR's own prefix.
      setError(String(err).replace(/^.*HubException: /, ""));
    }
  }

  function onCreate(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    e.currentTarget.reset();
    run(() => orderHub.createOrder(String(form.get("name")), Math.round(Number(form.get("total")) * 100)));
  }

  return (
    <main className="wide">
      <h1>Orders (live)</h1>

      <form onSubmit={onCreate}>
        <input name="name" placeholder="Customer name" />
        <input name="total" type="number" step="0.01" placeholder="Total (DKK)" />
        <button>Create order</button>
      </form>
      {error && <p className="error">{error}</p>}

      <div className="board">
        {columns.map((status) => <Column key={status} status={status} run={run} />)}
      </div>
    </main>
  );
}

function Column({ status, run }: { status: OrderStatus; run: (command: () => Promise<unknown>) => void }) {
  const orders = useLive(connection, () => orderHub.watchOrdersWithStatus(status), [status]);
  const next = columns[columns.indexOf(status) + 1];

  return (
    <section className="column">
      <h2>{OrderStatus[status]} <span className="hint">{orders.length}</span></h2>
      {orders.map((o: OrderDto) => (
        <article key={o.id} className="card">
          <div>#{o.id} {o.customerName}</div>
          <div className="hint num">{(o.totalCents / 100).toFixed(2)} DKK</div>
          <div className="card-actions">
            {next !== undefined && (
              <button onClick={() => run(() => orderHub.updateOrderStatus(o.id, next))}>→ {OrderStatus[next]}</button>
            )}
            <button onClick={() => run(() => orderHub.deleteOrder(o.id))}>Delete</button>
          </div>
        </article>
      ))}
    </section>
  );
}

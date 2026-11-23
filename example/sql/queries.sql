-- Queries (used for the snapshots of the Watch* streams) return the full row through sqlc.embed, so they share
-- one row type, Db.Order, which is also the type changes arrive as (Watch<Db.Order, ...> in server/OrderHub.cs).
-- Commands return at most the new id or the number of affected rows (0 = not found).

-- name: ListOrders :many
SELECT sqlc.embed(orders) FROM orders
ORDER BY id DESC;

-- name: ListOrdersByStatus :many
SELECT sqlc.embed(orders) FROM orders
WHERE status = sqlc.arg(status)::order_status
ORDER BY id DESC;

-- name: CreateOrder :one
INSERT INTO orders (customer_name, total_cents)
VALUES (trim(sqlc.arg(customer_name)::text), @total_cents)
RETURNING id;

-- name: UpdateOrderStatus :execrows
UPDATE orders SET status = sqlc.arg(status)::order_status
WHERE id = @id;

-- name: DeleteOrder :execrows
DELETE FROM orders
WHERE id = @id;

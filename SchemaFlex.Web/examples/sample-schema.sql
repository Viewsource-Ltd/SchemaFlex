-- Sample schema used to regenerate SchemaFlex.Web/examples/sample-erd.html on every
-- site build (see SchemaFlex.Web/build/build-site.mjs). A small SQLite database -
-- customers, orders, products, categories, addresses, order_items - with foreign
-- keys, check constraints, indexes and triggers, so the live example on the website
-- shows a real, representative diagram rather than a mock-up.
--
-- Deliberately not committed as a .db file: SQLite databases are binary and don't
-- diff cleanly, so the schema is kept here as SQL and the database is built fresh
-- from it at build time.

CREATE TABLE categories (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    parent_id INTEGER REFERENCES categories(id)
);

CREATE TABLE customers (
    id INTEGER PRIMARY KEY,
    email TEXT NOT NULL UNIQUE,
    full_name TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suspended', 'closed')),
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE addresses (
    id INTEGER PRIMARY KEY,
    customer_id INTEGER NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
    line1 TEXT NOT NULL,
    city TEXT NOT NULL,
    postcode TEXT NOT NULL,
    is_default INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX idx_addresses_default ON addresses(customer_id, is_default);

CREATE TABLE products (
    id INTEGER PRIMARY KEY,
    sku TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    category_id INTEGER REFERENCES categories(id),
    price_cents INTEGER NOT NULL CHECK (price_cents >= 0),
    stock_qty INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_products_category ON products(category_id);

CREATE TABLE orders (
    id INTEGER PRIMARY KEY,
    customer_id INTEGER NOT NULL REFERENCES customers(id),
    shipping_address_id INTEGER NOT NULL REFERENCES addresses(id),
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'paid', 'shipped', 'cancelled')),
    placed_at TEXT NOT NULL DEFAULT (datetime('now')),
    total_cents INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_orders_customer ON orders(customer_id);

CREATE TABLE order_items (
    id INTEGER PRIMARY KEY,
    order_id INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    product_id INTEGER NOT NULL REFERENCES products(id),
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    unit_price_cents INTEGER NOT NULL
);

CREATE INDEX idx_order_items_order ON order_items(order_id);

CREATE TRIGGER trg_customers_no_delete_with_orders
BEFORE DELETE ON customers
WHEN EXISTS (SELECT 1 FROM orders WHERE customer_id = OLD.id)
BEGIN
    SELECT RAISE(ABORT, 'cannot delete customer with existing orders');
END;

CREATE TRIGGER trg_orders_touch_total
AFTER INSERT ON order_items
BEGIN
    UPDATE orders
    SET total_cents = (
        SELECT COALESCE(SUM(quantity * unit_price_cents), 0)
        FROM order_items WHERE order_id = NEW.order_id
    )
    WHERE id = NEW.order_id;
END;

-- ----------------------------------------------------------------------------
-- Cart + combined checkout (docs/superpowers/specs/2026-09-13-cart-checkout-design.md §2)
-- ADDITIVE + IDEMPOTENT. Apply BEFORE deploying the cart code, then re-run once
-- AFTER the deploy (the copy at the bottom picks up any link the old code
-- minted in between). The manifests.checkout_* columns are dropped later by
-- db/cart-checkout-drop.sql.
-- ----------------------------------------------------------------------------
SET NOCOUNT ON;

IF OBJECT_ID('dbo.checkout_orders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.checkout_orders (
        square_order_id  VARCHAR(64)   NOT NULL PRIMARY KEY,
        kind             VARCHAR(16)   NOT NULL,                  -- 'link' | 'invoice'
        square_link_id   VARCHAR(64)   NULL,                      -- links only
        url              NVARCHAR(500) NULL,
        status           VARCHAR(16)   NOT NULL DEFAULT 'open',   -- open | paid | canceled
        subtotal_cents   BIGINT        NOT NULL CONSTRAINT DF_co_subtotal DEFAULT 0,   -- boxes only
        tax_cents        BIGINT        NOT NULL CONSTRAINT DF_co_tax      DEFAULT 0,   -- Square total_tax_money
        delivery_cents   BIGINT        NOT NULL CONSTRAINT DF_co_delivery DEFAULT 0,   -- Square total_service_charge_money
        -- Square total_money. Equals the three columns above EVERYWHERE EXCEPT ONE
        -- case, which is known and deliberate: fulfilment's recovery branch (the
        -- order == null block in CheckoutFulfillment) when Square returns no
        -- order-level tax. total_cents is then Square's own total and includes every
        -- line the buyer paid for, while subtotal_cents and tax_cents are derived from
        -- the checkout_order_boxes rows actually written -- and a box hard-deleted
        -- before the payment landed produces no row, so its money is in total_cents
        -- and in none of the parts.
        --
        -- Such a row IS the "missing box" case: the payment carries needs_refund with
        -- status PARTIAL_REFUND_FLAGGED and NO computed figure, because no figure
        -- derived from these columns is the whole debt.
        --
        -- Do NOT "repair" it by rewriting total_cents down to match the parts.
        -- total_cents is what the buyer was actually charged at Square and is the only
        -- correct basis for the refund.
        total_cents      BIGINT        NOT NULL,
        delivery_method  VARCHAR(16)   NOT NULL CONSTRAINT DF_co_method   DEFAULT 'pickup',  -- pickup | delivery | flea
        delivery_zip     VARCHAR(10)   NULL,
        delivery_address NVARCHAR(300) NULL,
        member_number    CHAR(7)       NULL,
        created_at       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        closed_at        DATETIME2     NULL,
        link_deleted_at  DATETIME2     NULL,                      -- Square confirmed (cancelled_order_id present)
        CONSTRAINT CK_checkout_orders_kind   CHECK (kind IN ('link','invoice')),
        CONSTRAINT CK_checkout_orders_status CHECK (status IN ('open','paid','canceled')),
        CONSTRAINT CK_checkout_orders_method CHECK (delivery_method IN ('pickup','delivery','flea'))
    );
    CREATE INDEX IX_co_status ON dbo.checkout_orders (status, kind, created_at);
END;
GO

-- Re-runnable column adds, for a database that already has the table from an
-- earlier apply of this script (spec §8 landed after the first version).
IF COL_LENGTH('dbo.checkout_orders', 'subtotal_cents') IS NULL
    ALTER TABLE dbo.checkout_orders ADD subtotal_cents BIGINT NOT NULL CONSTRAINT DF_co_subtotal DEFAULT 0;
IF COL_LENGTH('dbo.checkout_orders', 'tax_cents') IS NULL
    ALTER TABLE dbo.checkout_orders ADD tax_cents BIGINT NOT NULL CONSTRAINT DF_co_tax DEFAULT 0;
IF COL_LENGTH('dbo.checkout_orders', 'delivery_cents') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_cents BIGINT NOT NULL CONSTRAINT DF_co_delivery DEFAULT 0;
IF COL_LENGTH('dbo.checkout_orders', 'delivery_method') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_method VARCHAR(16) NOT NULL CONSTRAINT DF_co_method DEFAULT 'pickup';
IF COL_LENGTH('dbo.checkout_orders', 'delivery_zip') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_zip VARCHAR(10) NULL;
IF COL_LENGTH('dbo.checkout_orders', 'delivery_address') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_address NVARCHAR(300) NULL;
IF COL_LENGTH('dbo.checkout_orders', 'member_number') IS NULL
    ALTER TABLE dbo.checkout_orders ADD member_number CHAR(7) NULL;
GO

IF OBJECT_ID('dbo.checkout_order_boxes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.checkout_order_boxes (
        square_order_id  VARCHAR(64)      NOT NULL
            CONSTRAINT FK_cob_order REFERENCES dbo.checkout_orders (square_order_id),
        -- No FK on purpose, and the reason CHANGED in 2026-09: DeletePallet used to
        -- delete these rows, which silently dropped a deleted box's money out of the
        -- refund arithmetic -- the buyer paid for a box that no longer existed and the
        -- payment recorded as complete. It now marks them outcome='unavailable' and
        -- LEAVES them, so fulfilment's LEFT JOIN still sees the line and owes the buyer
        -- back. The row therefore OUTLIVES its manifest by design: do not add the FK,
        -- and do not "tidy up" orphans -- an orphan here is a debt we owe someone.
        manifest_id      UNIQUEIDENTIFIER NOT NULL,
        amount_cents     BIGINT           NOT NULL,   -- price at link time, EX tax
        tax_cents        BIGINT           NOT NULL CONSTRAINT DF_cob_tax DEFAULT 0,  -- this line's total_tax_money, from Square
        outcome          VARCHAR(16)      NULL,       -- NULL | 'sold' | 'unavailable'
        fulfilled_at     DATETIME2        NULL,
        CONSTRAINT PK_cob PRIMARY KEY (square_order_id, manifest_id),
        CONSTRAINT CK_cob_outcome CHECK (outcome IS NULL OR outcome IN ('sold','unavailable'))
    );
    CREATE INDEX IX_cob_manifest ON dbo.checkout_order_boxes (manifest_id);
END;
GO

IF COL_LENGTH('dbo.checkout_order_boxes', 'tax_cents') IS NULL
    ALTER TABLE dbo.checkout_order_boxes ADD tax_cents BIGINT NOT NULL CONSTRAINT DF_cob_tax DEFAULT 0;
GO

-- ----------------------------------------------------------------------------
-- Delivery radius as a list Rob can edit without a deploy (spec §8.4). A
-- curated allow-list, not a geocoder: one warehouse, one answer per zip, and
-- the call on a borderline zip is Rob's judgement, not a haversine.
-- The 'borderline' rows are seeded active = 0 until he confirms them.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.delivery_zips', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.delivery_zips (
        zip        VARCHAR(10)   NOT NULL PRIMARY KEY,
        fee_cents  INT           NOT NULL CONSTRAINT DF_dz_fee     DEFAULT 1000,
        active     BIT           NOT NULL CONSTRAINT DF_dz_active  DEFAULT 1,
        note       NVARCHAR(200) NULL,
        updated_at DATETIME2     NOT NULL CONSTRAINT DF_dz_updated DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- Seed only if empty, so re-running never undoes Rob's edits.
IF NOT EXISTS (SELECT 1 FROM dbo.delivery_zips)
BEGIN
    INSERT INTO dbo.delivery_zips (zip, active, note) VALUES
        ('27587', 1, 'Wake Forest'),        ('27588', 1, 'Wake Forest PO boxes'),
        ('27571', 1, 'Rolesville'),         ('27596', 1, 'Youngsville'),
        ('27525', 1, 'Franklinton'),        ('27522', 1, 'Creedmoor'),
        ('27614', 1, 'N Raleigh'),          ('27616', 1, 'N Raleigh'),
        ('27615', 1, 'N Raleigh'),          ('27613', 1, 'N Raleigh'),
        ('27617', 1, 'N Raleigh'),          ('27609', 1, 'N Raleigh'),
        ('27604', 1, 'NE Raleigh'),         ('27545', 1, 'Knightdale'),
        ('27591', 1, 'Wendell'),
        -- Borderline: inactive until Rob says yes (spec §8.4)
        ('27601', 0, 'Downtown Raleigh - confirm with Rob'),
        ('27605', 0, 'Raleigh - confirm with Rob'),
        ('27606', 0, 'Raleigh - confirm with Rob'),
        ('27607', 0, 'Raleigh - confirm with Rob'),
        ('27608', 0, 'Raleigh - confirm with Rob'),
        ('27610', 0, 'Raleigh - confirm with Rob'),
        ('27612', 0, 'Raleigh - confirm with Rob'),
        ('27597', 0, 'Zebulon - confirm with Rob'),
        ('27549', 0, 'Louisburg - confirm with Rob'),
        ('27560', 0, 'Morrisville - confirm with Rob'),
        ('27703', 0, 'Durham - confirm with Rob'),
        ('27529', 0, 'Garner - confirm with Rob');
END;
GO

IF COL_LENGTH('dbo.payments', 'refund_due_cents') IS NULL
    ALTER TABLE dbo.payments ADD refund_due_cents BIGINT NULL;
IF COL_LENGTH('dbo.payments', 'refunded_cents') IS NULL
    ALTER TABLE dbo.payments ADD refunded_cents BIGINT NOT NULL CONSTRAINT DF_payments_refunded DEFAULT 0;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.checkout_orders       TO nsl_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.checkout_order_boxes  TO nsl_api;
GRANT SELECT                          ON dbo.delivery_zips        TO nsl_api;
GO

-- Copy existing per-box links / invoices (old model) into the new tables.
-- Amount = current ask price (the old code never stored the link amount).
IF COL_LENGTH('dbo.manifests', 'checkout_order_id') IS NOT NULL
BEGIN
    -- Legacy links were minted before tax existed: subtotal = total, tax = 0,
    -- delivery = 0, method = pickup. Their buyers were not charged tax and we
    -- must not invent it retrospectively.
    INSERT INTO dbo.checkout_orders (square_order_id, kind, square_link_id, url, status, subtotal_cents, total_cents, created_at)
    SELECT m.checkout_order_id,
           CASE WHEN m.invoice_id IS NOT NULL THEN 'invoice' ELSE 'link' END,
           m.checkout_link_id,
           COALESCE(m.checkout_url, m.invoice_url),
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN 'paid' ELSE 'open' END,
           CAST(ROUND(COALESCE(m.sale_price, m.list_price, 0) * 100, 0) AS BIGINT),
           CAST(ROUND(COALESCE(m.sale_price, m.list_price, 0) * 100, 0) AS BIGINT),
           COALESCE(m.checkout_created_at, SYSUTCDATETIME())
    FROM dbo.manifests m
    WHERE m.checkout_order_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.checkout_orders o WHERE o.square_order_id = m.checkout_order_id);

    INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, outcome, fulfilled_at)
    SELECT m.checkout_order_id, m.id,
           CAST(ROUND(COALESCE(m.sale_price, m.list_price, 0) * 100, 0) AS BIGINT),
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN 'sold' ELSE NULL END,
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN m.sold_at ELSE NULL END
    FROM dbo.manifests m
    WHERE m.checkout_order_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b
                      WHERE b.square_order_id = m.checkout_order_id AND b.manifest_id = m.id);
END;
GO

-- One row per Square refund we have applied to payments.refunded_cents, so a
-- replayed refund.updated (or the admin endpoint and the webhook both seeing
-- the same refund) can never double-count. The refund id is the primary key:
-- the INSERT itself is the dedupe, and only a NEW row is allowed to move the
-- running total on dbo.payments.
IF OBJECT_ID('dbo.payment_refunds', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.payment_refunds (
        square_refund_id  VARCHAR(64) NOT NULL PRIMARY KEY,
        square_payment_id VARCHAR(64) NOT NULL,
        amount_cents      BIGINT      NOT NULL,
        created_at        DATETIME2   NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_payment_refunds_payment ON dbo.payment_refunds (square_payment_id);
END;
GO

GRANT SELECT, INSERT ON dbo.payment_refunds TO nsl_api;
GO

-- A payment whose debt we DECLINED TO PRICE. Fulfilment leaves refund_due_cents
-- NULL when the order lines cannot account for what the buyer paid, because no
-- figure derived from those lines is the whole debt -- so the staff page refuses
-- to offer a number and the refund endpoint refuses to send one. Staff settle it
-- by hand in the Square Dashboard and then acknowledge the row here.
--
-- WHY THESE TWO COLUMNS AND NOT JUST THE STATUS: "we declined to price this" was
-- previously recorded only IN the status, so the first partial refund overwrote
-- it and the fact was erased -- after which the endpoint would once again offer
-- the rest of the payment on a row nobody could price. acknowledged_at is a
-- FACT ABOUT THE ROW, not a stage it passes through, so it survives every later
-- status change. acknowledged_by is who took responsibility: this clears a debt
-- without moving money, and the one question afterwards is always who decided.
IF COL_LENGTH('dbo.payments', 'acknowledged_at') IS NULL
    ALTER TABLE dbo.payments ADD acknowledged_at DATETIME2 NULL;
IF COL_LENGTH('dbo.payments', 'acknowledged_by') IS NULL
    ALTER TABLE dbo.payments ADD acknowledged_by NVARCHAR(200) NULL;
GO

PRINT 'cart-checkout: checkout_orders + checkout_order_boxes + delivery_zips + payments.refund columns + payment_refunds + acknowledge columns ready.';

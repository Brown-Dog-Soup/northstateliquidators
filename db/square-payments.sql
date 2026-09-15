-- ----------------------------------------------------------------------------
-- Square payments (SQUARE-INTEGRATION.md)
--
-- manifests gains the per-box checkout-link columns (one single-use Square
-- payment link per box; order_id is the correlation key the webhook matches
-- on). dbo.payments is the audit trail — one row per Square payment, with a
-- UNIQUE square_payment_id so webhook retries/replays can never double-log.
-- ----------------------------------------------------------------------------
-- SUPERSEDED 2026-09: the manifests.checkout_* columns below are replaced by
-- dbo.checkout_orders / checkout_order_boxes (db/cart-checkout.sql) and dropped
-- by db/cart-checkout-drop.sql. Do NOT re-apply this file on prod.
IF COL_LENGTH('dbo.manifests', 'checkout_link_id') IS NULL
    ALTER TABLE dbo.manifests ADD
        checkout_link_id    VARCHAR(64)    NULL,
        checkout_order_id   VARCHAR(64)    NULL,
        checkout_url        NVARCHAR(500)  NULL,
        checkout_created_at DATETIME2      NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'payments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.payments (
        id                UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
        square_payment_id VARCHAR(64)      NOT NULL,
        square_order_id   VARCHAR(64)      NULL,
        -- Box it sold, kept only for single-box orders. NULL no longer means
        -- "unmatched": since db/cart-checkout.sql a cart order can hold several
        -- boxes and this column is NULL for every one of them. The boxes an order
        -- sold are dbo.checkout_order_boxes; "needs attention" is needs_refund = 1
        -- or status = 'UNMATCHED', never a NULL here.
        manifest_id       UNIQUEIDENTIFIER NULL,
        amount_cents      BIGINT           NULL,
        currency          VARCHAR(8)       NULL,
        status            VARCHAR(40)      NOT NULL,   -- COMPLETED | REFUND_FLAGGED | ...
        needs_refund      BIT              NOT NULL DEFAULT 0,  -- payment landed on an already-sold/canceled box
        event_json        NVARCHAR(MAX)    NULL,
        created_at        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_payments_square_payment UNIQUE (square_payment_id)
    );
    CREATE INDEX IX_payments_manifest ON dbo.payments (manifest_id) WHERE manifest_id IS NOT NULL;
END;
GO

GRANT SELECT, INSERT, UPDATE ON dbo.payments TO nsl_api;

PRINT 'square-payments: manifests checkout columns + dbo.payments ready.';

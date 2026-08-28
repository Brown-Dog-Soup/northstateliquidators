-- ----------------------------------------------------------------------------
-- Square payments (SQUARE-INTEGRATION.md)
--
-- manifests gains the per-box checkout-link columns (one single-use Square
-- payment link per box; order_id is the correlation key the webhook matches
-- on). dbo.payments is the audit trail — one row per Square payment, with a
-- UNIQUE square_payment_id so webhook retries/replays can never double-log.
-- ----------------------------------------------------------------------------
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
        manifest_id       UNIQUEIDENTIFIER NULL,       -- box it sold; NULL = unmatched (needs attention)
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

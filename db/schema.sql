-- ============================================================================
-- NSL Inventory Pipeline — schema
-- Apply to sqldb-nsl-prod after Bicep deploy.
-- Run via: sqlcmd -S <sql-server-fqdn> -d sqldb-nsl-prod -G -i schema.sql
-- (Uses Active Directory authentication; you must be the SQL Entra admin.)
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- manifests
-- One row per pallet/load received.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'manifests' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.manifests (
        id              UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWID() PRIMARY KEY,
        source          NVARCHAR(200)        NULL,    -- auction lot #, vendor, truck #, etc.
        pallet_reference NVARCHAR(200)       NULL,    -- internal/short pallet ID
        received_date   DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        status          VARCHAR(40)          NOT NULL DEFAULT 'receiving',
            -- receiving | enriching | ready | lotted | individualized | sold
        total_cost      DECIMAL(12, 2)       NULL,
        sell_mode       VARCHAR(20)          NOT NULL DEFAULT 'undecided',
            -- undecided | lot | individual | mixed
        notes           NVARCHAR(MAX)        NULL,
        created_at      DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at      DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_manifests_status     CHECK (status IN ('receiving','enriching','ready','lotted','individualized','sold')),
        CONSTRAINT CK_manifests_sell_mode  CHECK (sell_mode IN ('undecided','lot','individual','mixed'))
    );
END;
GO

-- ----------------------------------------------------------------------------
-- line_items
-- One row per scanned unit; FK to manifests.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'line_items' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.line_items (
        id              UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWID() PRIMARY KEY,
        manifest_id     UNIQUEIDENTIFIER     NOT NULL,
        upc             VARCHAR(20)          NULL,
        lpn             VARCHAR(40)          NULL,
        asin            VARCHAR(20)          NULL,
        qty             INT                  NOT NULL DEFAULT 1,
        condition       VARCHAR(40)          NULL,    -- new | open_box | damaged | untested | customer_return | salvage
        photo_blob_url  NVARCHAR(2000)       NULL,
        enrich_status   VARCHAR(20)          NOT NULL DEFAULT 'pending',
            -- pending | hit | miss | partial | error
        enrich_source   VARCHAR(40)          NULL,    -- lpn_catalog | go_upc | upcitemdb | keepa | ebay | ai_vision | manual
        title           NVARCHAR(500)        NULL,
        description     NVARCHAR(MAX)        NULL,
        brand           NVARCHAR(200)        NULL,
        category        NVARCHAR(200)        NULL,
        est_msrp        DECIMAL(12, 2)       NULL,
        est_resale      DECIMAL(12, 2)       NULL,
        unit_cost       DECIMAL(12, 4)       NULL,    -- our cost per unit (usually copied from manifest line on import, or distributed from total_cost)
        shopify_product_id BIGINT            NULL,
        shopify_variant_id BIGINT            NULL,
        sold_at         DATETIME2            NULL,
        created_at      DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        enriched_at     DATETIME2            NULL,
        pushed_at       DATETIME2            NULL,
        notes           NVARCHAR(MAX)        NULL,
        CONSTRAINT FK_line_items_manifest FOREIGN KEY (manifest_id) REFERENCES dbo.manifests(id),
        CONSTRAINT CK_line_items_enrich_status CHECK (enrich_status IN ('pending','hit','miss','partial','error'))
    );

    CREATE INDEX IX_line_items_manifest        ON dbo.line_items (manifest_id);
    CREATE INDEX IX_line_items_lpn             ON dbo.line_items (lpn) WHERE lpn IS NOT NULL;
    CREATE INDEX IX_line_items_upc             ON dbo.line_items (upc) WHERE upc IS NOT NULL;
    CREATE INDEX IX_line_items_enrich_status   ON dbo.line_items (enrich_status, manifest_id);
    CREATE INDEX IX_line_items_shopify_product ON dbo.line_items (shopify_product_id) WHERE shopify_product_id IS NOT NULL;
END;
GO

-- ----------------------------------------------------------------------------
-- lpn_catalog
-- Pre-loaded from Amazon liquidation manifest XLSX files.
-- LPNs do not exist in any public UPC database; this is our private mirror.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'lpn_catalog' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.lpn_catalog (
        lpn             VARCHAR(40)          NOT NULL PRIMARY KEY,
        asin            VARCHAR(20)          NULL,
        upc             VARCHAR(20)          NULL,
        ean             VARCHAR(20)          NULL,
        title           NVARCHAR(500)        NULL,
        description     NVARCHAR(MAX)        NULL,
        brand           NVARCHAR(200)        NULL,
        category        NVARCHAR(200)        NULL,
        subcategory     NVARCHAR(200)        NULL,
        msrp            DECIMAL(12, 2)       NULL,
        unit_cost       DECIMAL(12, 4)       NULL,
        condition       VARCHAR(40)          NULL,    -- USED_GOOD | NEW | CUSTOMER_RETURN | SALVAGE | etc. (from manifest)
        qty_in_manifest INT                  NULL,
        seller_category NVARCHAR(200)        NULL,
        product_class   NVARCHAR(200)        NULL,
        order_number    NVARCHAR(100)        NULL,    -- Amazon Order # from manifest, e.g. "AMZ0N-OJ5-4G8R"
        pallet_id       NVARCHAR(200)        NULL,    -- e.g. "LIQ:3PL:PALLET:LNRM:PAL-49825"
        lot_id          NVARCHAR(200)        NULL,    -- e.g. "AMZ_3PL_20251121_020"
        source_manifest NVARCHAR(500)        NOT NULL,    -- filename of the XLSX
        source_pallet_ref NVARCHAR(200)      NULL,
        imported_at     DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        last_seen_at    DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_lpn_catalog_asin    ON dbo.lpn_catalog (asin) WHERE asin IS NOT NULL;
    CREATE INDEX IX_lpn_catalog_upc     ON dbo.lpn_catalog (upc) WHERE upc IS NOT NULL;
    CREATE INDEX IX_lpn_catalog_pallet  ON dbo.lpn_catalog (pallet_id) WHERE pallet_id IS NOT NULL;
END;
GO

-- ----------------------------------------------------------------------------
-- manifest_imports
-- Audit of every XLSX file ingested. SHA-256 deduplicates re-imports.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'manifest_imports' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.manifest_imports (
        id              UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWID() PRIMARY KEY,
        filename        NVARCHAR(500)        NOT NULL,
        sha256          CHAR(64)             NOT NULL UNIQUE,
        pallet_reference NVARCHAR(200)       NULL,
        order_number    NVARCHAR(100)        NULL,
        row_count       INT                  NOT NULL,
        rows_inserted   INT                  NOT NULL DEFAULT 0,
        rows_updated    INT                  NOT NULL DEFAULT 0,
        rows_skipped    INT                  NOT NULL DEFAULT 0,
        unmapped_columns NVARCHAR(MAX)       NULL,    -- JSON array of column names we didn't know how to map
        imported_at     DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        imported_by     NVARCHAR(200)        NULL,    -- UPN of the user (or 'system' for blob-trigger)
        archive_blob_url NVARCHAR(2000)      NULL     -- where we saved the original file after parse
    );

    CREATE INDEX IX_manifest_imports_imported_at ON dbo.manifest_imports (imported_at DESC);
END;
GO

-- ----------------------------------------------------------------------------
-- enrichment_log
-- Per-API audit row for each enrichment attempt; lets us tune hit rate.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'enrichment_log' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.enrichment_log (
        id              BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        line_item_id    UNIQUEIDENTIFIER     NOT NULL,
        api_name        VARCHAR(40)          NOT NULL,    -- lpn_catalog | go_upc | upcitemdb | keepa | ebay | ai_vision
        request_value   NVARCHAR(200)        NULL,         -- the LPN/UPC/etc we asked about
        outcome         VARCHAR(20)          NOT NULL,     -- hit | miss | error | partial
        latency_ms      INT                  NULL,
        raw_response    NVARCHAR(MAX)        NULL,         -- JSON or trimmed payload
        error_message   NVARCHAR(MAX)        NULL,
        called_at       DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_enrichment_log_line_item FOREIGN KEY (line_item_id) REFERENCES dbo.line_items(id),
        CONSTRAINT CK_enrichment_log_outcome CHECK (outcome IN ('hit','miss','error','partial'))
    );

    CREATE INDEX IX_enrichment_log_line_item ON dbo.enrichment_log (line_item_id, called_at DESC);
    CREATE INDEX IX_enrichment_log_api_outcome ON dbo.enrichment_log (api_name, outcome, called_at DESC);
END;
GO

-- ----------------------------------------------------------------------------
-- Views: handy for the review dashboard
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'v_manifest_summary') DROP VIEW dbo.v_manifest_summary;
GO
CREATE VIEW dbo.v_manifest_summary AS
SELECT
    m.id                AS manifest_id,
    m.source,
    m.pallet_reference,
    m.received_date,
    m.status,
    m.sell_mode,
    m.total_cost,
    COUNT(li.id)        AS total_items,
    SUM(li.qty)         AS total_units,
    SUM(li.est_msrp)    AS total_msrp,
    SUM(li.est_resale)  AS total_est_resale,
    SUM(CASE WHEN li.enrich_status = 'hit'     THEN 1 ELSE 0 END) AS items_enriched,
    SUM(CASE WHEN li.enrich_status = 'pending' THEN 1 ELSE 0 END) AS items_pending,
    SUM(CASE WHEN li.enrich_status = 'miss'    THEN 1 ELSE 0 END) AS items_missed,
    SUM(CASE WHEN li.shopify_product_id IS NOT NULL THEN 1 ELSE 0 END) AS items_pushed_to_shopify
FROM dbo.manifests m
LEFT JOIN dbo.line_items li ON li.manifest_id = m.id
GROUP BY m.id, m.source, m.pallet_reference, m.received_date, m.status, m.sell_mode, m.total_cost;
GO

PRINT 'Schema applied.';

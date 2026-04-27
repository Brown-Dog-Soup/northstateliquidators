-- ============================================================================
-- Stored procedures used by the NSL Receiving Power Apps canvas app.
-- Apply with:
--   sqlcmd-equivalent of `power-apps-procs.sql` against sqldb-nsl-prod
-- ============================================================================

-- ----------------------------------------------------------------------------
-- sp_LookupCode
-- Called by Power Apps when a barcode is scanned. Tries (in order):
--   1. lpn_catalog.lpn      (Amazon LPN — preloaded from manifests)
--   2. lpn_catalog.upc      (any UPC found in a prior manifest)
--   3. lpn_catalog.asin     (Amazon ASIN exact match)
-- Returns at most one row with a `match_source` discriminator so the UI can
-- show where the answer came from.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_LookupCode', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_LookupCode;
GO
CREATE PROCEDURE dbo.sp_LookupCode
    @code NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @c NVARCHAR(50) = LTRIM(RTRIM(@code));

    SELECT TOP 1
        'lpn'           AS match_source,
        c.lpn,
        c.asin,
        c.upc,
        c.title,
        c.brand,
        c.category,
        c.subcategory,
        c.msrp,
        c.unit_cost,
        c.condition,
        c.qty_in_manifest,
        c.pallet_id,
        c.lot_id,
        c.order_number
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @c
    UNION ALL
    SELECT TOP 1
        'upc'           AS match_source,
        c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
        c.msrp, c.unit_cost, c.condition, c.qty_in_manifest, c.pallet_id,
        c.lot_id, c.order_number
    FROM dbo.lpn_catalog c
    WHERE c.upc = @c AND NOT EXISTS (SELECT 1 FROM dbo.lpn_catalog WHERE lpn = @c)
    UNION ALL
    SELECT TOP 1
        'asin'          AS match_source,
        c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
        c.msrp, c.unit_cost, c.condition, c.qty_in_manifest, c.pallet_id,
        c.lot_id, c.order_number
    FROM dbo.lpn_catalog c
    WHERE c.asin = @c AND NOT EXISTS (SELECT 1 FROM dbo.lpn_catalog WHERE lpn = @c OR upc = @c);
END;
GO

-- ----------------------------------------------------------------------------
-- sp_RecordScan
-- Inserts a line_items row when a receiver confirms a scan.
-- If @manifest_id is null, picks the most-recent manifests row so receiving
-- can start without an explicit manifest pick — convenient for v1.
-- Returns the new line_items.id so Power Apps can navigate to a confirmation
-- screen.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_RecordScan', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RecordScan;
GO
CREATE PROCEDURE dbo.sp_RecordScan
    @manifest_id UNIQUEIDENTIFIER = NULL,
    @code        NVARCHAR(50),
    @qty         INT             = 1,
    @condition   VARCHAR(40)     = NULL,
    @notes       NVARCHAR(MAX)   = NULL,
    @photo_url   NVARCHAR(2000)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Resolve manifest: explicit > most-recent
    IF @manifest_id IS NULL
        SELECT TOP 1 @manifest_id = id
        FROM dbo.manifests
        ORDER BY received_date DESC, created_at DESC;

    IF @manifest_id IS NULL
    BEGIN
        RAISERROR('No manifest specified and none exists in dbo.manifests. Create a manifest row before scanning.', 16, 1);
        RETURN;
    END;

    -- Look up the catalog row to seed enrichment fields
    DECLARE
        @lpn        VARCHAR(40),  @asin VARCHAR(20),  @upc VARCHAR(20),
        @title      NVARCHAR(500), @brand NVARCHAR(200), @category NVARCHAR(200),
        @msrp       DECIMAL(12,2), @unit_cost DECIMAL(12,4),
        @cat_cond   VARCHAR(40);

    SELECT TOP 1
        @lpn = c.lpn, @asin = c.asin, @upc = c.upc,
        @title = c.title, @brand = c.brand, @category = c.category,
        @msrp = c.msrp, @unit_cost = c.unit_cost, @cat_cond = c.condition
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @code OR c.upc = @code OR c.asin = @code;

    -- Resolve final code-shape: if it looks like an LPN keep it as such,
    -- otherwise treat as a UPC.
    DECLARE @id UNIQUEIDENTIFIER = NEWID();
    DECLARE @scan_lpn VARCHAR(40) = CASE WHEN @code LIKE 'LPN%' OR @lpn IS NOT NULL THEN COALESCE(@lpn, @code) END;
    DECLARE @scan_upc VARCHAR(20) = CASE WHEN @code NOT LIKE 'LPN%' AND @lpn IS NULL THEN @code ELSE @upc END;

    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, brand, category, est_msrp, unit_cost, notes,
         created_at, enriched_at)
    VALUES
        (@id, @manifest_id, @scan_upc, @scan_lpn, @asin, @qty,
         COALESCE(@condition, @cat_cond),
         @photo_url,
         CASE WHEN @lpn IS NOT NULL THEN 'hit' ELSE 'pending' END,
         CASE WHEN @lpn IS NOT NULL THEN 'lpn_catalog' ELSE NULL END,
         @title, @brand, @category, @msrp, @unit_cost, @notes,
         SYSUTCDATETIME(),
         CASE WHEN @lpn IS NOT NULL THEN SYSUTCDATETIME() ELSE NULL END);

    SELECT
        @id                 AS line_item_id,
        @manifest_id        AS manifest_id,
        CASE WHEN @lpn IS NOT NULL THEN 'hit' ELSE 'pending' END AS enrich_status,
        @title              AS title,
        @brand              AS brand,
        @msrp               AS msrp,
        COALESCE(@condition, @cat_cond) AS condition;
END;
GO

-- ----------------------------------------------------------------------------
-- v_recent_scans — convenience view used by the Power Apps "history" screen
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.v_recent_scans', 'V') IS NOT NULL DROP VIEW dbo.v_recent_scans;
GO
CREATE VIEW dbo.v_recent_scans AS
SELECT TOP 200
    li.id              AS line_item_id,
    li.manifest_id,
    m.pallet_reference,
    li.lpn,
    li.upc,
    li.asin,
    li.qty,
    li.condition,
    li.title,
    li.brand,
    li.est_msrp,
    li.unit_cost,
    li.enrich_status,
    li.created_at
FROM dbo.line_items li
LEFT JOIN dbo.manifests m ON m.id = li.manifest_id
ORDER BY li.created_at DESC;
GO

-- ----------------------------------------------------------------------------
-- Grant nsl_api access to the new procs/view (Power Apps signs in as nsl_api)
-- ----------------------------------------------------------------------------
GRANT EXECUTE ON dbo.sp_LookupCode TO nsl_api;
GRANT EXECUTE ON dbo.sp_RecordScan TO nsl_api;
GRANT SELECT ON dbo.v_recent_scans TO nsl_api;
GRANT SELECT ON dbo.lpn_catalog TO nsl_api;
GRANT SELECT, INSERT, UPDATE ON dbo.line_items TO nsl_api;
GRANT SELECT, INSERT ON dbo.manifests TO nsl_api;

PRINT 'Power Apps procs + view applied.';

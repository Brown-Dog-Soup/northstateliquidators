-- ============================================================================
-- Adds @sell_price parameter to sp_RecordScan so the receiver can capture a
-- target sell price at scan time (auto-suggested from a condition multiplier
-- applied to the catalog/UPC reference price; receiver can override).
--
-- Stores into the existing line_items.est_resale column.
--
-- Apply against sqldb-nsl-prod with the same connection used for
-- power-apps-procs.sql.
-- ============================================================================

IF OBJECT_ID('dbo.sp_RecordScan', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RecordScan;
GO
CREATE PROCEDURE dbo.sp_RecordScan
    @manifest_id UNIQUEIDENTIFIER = NULL,
    @code        NVARCHAR(50),
    @qty         INT             = 1,
    @condition   VARCHAR(40)     = NULL,
    @notes       NVARCHAR(MAX)   = NULL,
    @photo_url   NVARCHAR(2000)  = NULL,
    @sell_price  DECIMAL(12,2)   = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @manifest_id IS NULL
        SELECT TOP 1 @manifest_id = id
        FROM dbo.manifests
        ORDER BY received_date DESC, created_at DESC;

    IF @manifest_id IS NULL
    BEGIN
        RAISERROR('No manifest specified and none exists in dbo.manifests. Create a manifest row before scanning.', 16, 1);
        RETURN;
    END;

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

    DECLARE @id UNIQUEIDENTIFIER = NEWID();
    DECLARE @scan_lpn VARCHAR(40) = CASE WHEN @code LIKE 'LPN%' OR @lpn IS NOT NULL THEN COALESCE(@lpn, @code) END;
    DECLARE @scan_upc VARCHAR(20) = CASE WHEN @code NOT LIKE 'LPN%' AND @lpn IS NULL THEN @code ELSE @upc END;

    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, brand, category, est_msrp, est_resale, unit_cost, notes,
         created_at, enriched_at)
    VALUES
        (@id, @manifest_id, @scan_upc, @scan_lpn, @asin, @qty,
         COALESCE(@condition, @cat_cond),
         @photo_url,
         CASE WHEN @lpn IS NOT NULL THEN 'hit' ELSE 'pending' END,
         CASE WHEN @lpn IS NOT NULL THEN 'lpn_catalog' ELSE NULL END,
         @title, @brand, @category, @msrp, @sell_price, @unit_cost, @notes,
         SYSUTCDATETIME(),
         CASE WHEN @lpn IS NOT NULL THEN SYSUTCDATETIME() ELSE NULL END);

    SELECT
        @id                 AS line_item_id,
        @manifest_id        AS manifest_id,
        CASE WHEN @lpn IS NOT NULL THEN 'hit' ELSE 'pending' END AS enrich_status,
        @title              AS title,
        @brand              AS brand,
        @msrp               AS msrp,
        @sell_price         AS sell_price,
        COALESCE(@condition, @cat_cond) AS condition;
END;
GO

GRANT EXECUTE ON dbo.sp_RecordScan TO nsl_api;

PRINT 'sp_RecordScan upgraded with @sell_price param.';

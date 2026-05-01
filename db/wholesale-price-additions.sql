-- ============================================================================
-- Adds Wholesale Price (the third leg of MSRP / COST / PRICE on the receiving
-- page) to lpn_catalog and line_items, and refreshes sp_LookupCode,
-- sp_RecordScan, and v_pallets to flow it through the pipeline.
--
-- MSRP   = Unit Retail   (already on lpn_catalog.msrp)
-- COST   = Unit Cost     (already on lpn_catalog.unit_cost)
-- PRICE  = Wholesale Price  ← NEW column added here
--
-- Apply against sqldb-nsl-prod after the base schema and the previous
-- power-apps-procs.sql / archive-and-category-additions.sql migrations.
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- 1. Catalog: store the manifest's "Wholesale Price" column alongside MSRP / Unit Cost
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.lpn_catalog', 'wholesale_price') IS NULL
    ALTER TABLE dbo.lpn_catalog ADD wholesale_price DECIMAL(12,2) NULL;
GO

-- ----------------------------------------------------------------------------
-- 2. line_items: record the wholesale price at scan time (sticky to that row
--    even if the catalog later changes). Independent of est_resale, which is
--    the receiver-set sell price (condition multiplier).
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.line_items', 'wholesale_price') IS NULL
    ALTER TABLE dbo.line_items ADD wholesale_price DECIMAL(12,2) NULL;
GO

-- ----------------------------------------------------------------------------
-- 3. sp_LookupCode — return wholesale_price so the receiving page can display
--    it before the scan is recorded.
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
        c.lpn, c.asin, c.upc,
        c.title, c.brand, c.category, c.subcategory,
        c.msrp, c.unit_cost, c.wholesale_price,
        c.condition, c.qty_in_manifest,
        c.pallet_id, c.lot_id, c.order_number,
        c.product_image_url AS image_url
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @c
    UNION ALL
    SELECT TOP 1
        'upc'           AS match_source,
        c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
        c.msrp, c.unit_cost, c.wholesale_price,
        c.condition, c.qty_in_manifest, c.pallet_id, c.lot_id, c.order_number,
        c.product_image_url AS image_url
    FROM dbo.lpn_catalog c
    WHERE c.upc = @c AND NOT EXISTS (SELECT 1 FROM dbo.lpn_catalog WHERE lpn = @c)
    UNION ALL
    SELECT TOP 1
        'asin'          AS match_source,
        c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
        c.msrp, c.unit_cost, c.wholesale_price,
        c.condition, c.qty_in_manifest, c.pallet_id, c.lot_id, c.order_number,
        c.product_image_url AS image_url
    FROM dbo.lpn_catalog c
    WHERE c.asin = @c AND NOT EXISTS (SELECT 1 FROM dbo.lpn_catalog WHERE lpn = @c OR upc = @c);
END;
GO

-- ----------------------------------------------------------------------------
-- 4. sp_RecordScan — capture wholesale_price into line_items at scan time.
--    Uses catalog value when present; @arg_wholesale_price provides a fallback
--    for non-catalog hits (e.g. UPCitemdb returns no wholesale, this stays NULL).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_RecordScan', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RecordScan;
GO
CREATE PROCEDURE dbo.sp_RecordScan
    @manifest_id      UNIQUEIDENTIFIER = NULL,
    @code             NVARCHAR(50),
    @qty              INT             = 1,
    @condition        VARCHAR(40)     = NULL,
    @notes            NVARCHAR(MAX)   = NULL,
    @photo_url        NVARCHAR(2000)  = NULL,
    @sell_price       DECIMAL(12,2)   = NULL,
    @arg_title        NVARCHAR(500)   = NULL,
    @arg_brand        NVARCHAR(200)   = NULL,
    @arg_category     NVARCHAR(200)   = NULL,
    @arg_msrp         DECIMAL(12,2)   = NULL,
    @arg_match_source VARCHAR(40)     = NULL,
    @arg_wholesale_price DECIMAL(12,2) = NULL    -- NEW
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
        @wholesale  DECIMAL(12,2),
        @cat_cond   VARCHAR(40);

    SELECT TOP 1
        @lpn = c.lpn, @asin = c.asin, @upc = c.upc,
        @title = c.title, @brand = c.brand, @category = c.category,
        @msrp = c.msrp, @unit_cost = c.unit_cost,
        @wholesale = c.wholesale_price,
        @cat_cond = c.condition
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @code OR c.upc = @code OR c.asin = @code;

    DECLARE
        @final_title    NVARCHAR(500) = COALESCE(@title,    @arg_title),
        @final_brand    NVARCHAR(200) = COALESCE(@brand,    @arg_brand),
        @final_category NVARCHAR(200) = COALESCE(@category, @arg_category),
        @final_msrp     DECIMAL(12,2) = COALESCE(@msrp,     @arg_msrp),
        @final_wholesale DECIMAL(12,2) = COALESCE(@wholesale, @arg_wholesale_price);

    DECLARE
        @enrich_status VARCHAR(20) =
            CASE
                WHEN @lpn IS NOT NULL                              THEN 'hit'
                WHEN @final_title IS NOT NULL                      THEN 'hit'
                ELSE 'pending'
            END,
        @enrich_source VARCHAR(40) =
            CASE
                WHEN @lpn IS NOT NULL                              THEN 'lpn_catalog'
                WHEN @final_title IS NOT NULL                      THEN @arg_match_source
                ELSE NULL
            END;

    DECLARE @id UNIQUEIDENTIFIER = NEWID();
    DECLARE @scan_lpn VARCHAR(40) = CASE WHEN @code LIKE 'LPN%' OR @lpn IS NOT NULL THEN COALESCE(@lpn, @code) END;
    DECLARE @scan_upc VARCHAR(20) = CASE WHEN @code NOT LIKE 'LPN%' AND @lpn IS NULL THEN @code ELSE @upc END;

    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
         created_at, enriched_at)
    VALUES
        (@id, @manifest_id, @scan_upc, @scan_lpn, @asin, @qty,
         COALESCE(@condition, @cat_cond),
         @photo_url,
         @enrich_status,
         @enrich_source,
         @final_title, @final_brand, @final_category, @final_msrp,
         @sell_price, @unit_cost, @final_wholesale, @notes,
         SYSUTCDATETIME(),
         CASE WHEN @enrich_status = 'hit' THEN SYSUTCDATETIME() ELSE NULL END);

    SELECT
        @id                 AS line_item_id,
        @manifest_id        AS manifest_id,
        @enrich_status      AS enrich_status,
        @final_title        AS title,
        @final_brand        AS brand,
        @final_msrp         AS msrp,
        @final_wholesale    AS wholesale_price,
        @sell_price         AS sell_price,
        COALESCE(@condition, @cat_cond) AS condition;
END;
GO

-- ----------------------------------------------------------------------------
-- 5. v_pallets — add total_wholesale roll-up so the admin gallery + pallet
--    detail can show pallet-level totals.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.v_pallets', 'V') IS NOT NULL DROP VIEW dbo.v_pallets;
GO
CREATE VIEW dbo.v_pallets AS
SELECT
    m.id                    AS manifest_id,
    m.pallet_number,
    m.display_name,
    m.source,
    m.pallet_reference,
    m.received_date,
    m.status,
    m.sell_mode,
    m.category,
    m.archived_at,
    m.total_cost,
    m.photo_url,
    COUNT(li.id)            AS item_count,
    SUM(li.qty)             AS unit_count,
    SUM(li.est_msrp * li.qty)         AS total_msrp,
    SUM(li.unit_cost * li.qty)        AS total_cost_units,
    SUM(li.wholesale_price * li.qty)  AS total_wholesale,
    SUM(li.est_resale * li.qty)       AS total_est_resale,
    SUM(CASE WHEN li.enrich_status = 'hit' THEN 1 ELSE 0 END) AS items_enriched
FROM dbo.manifests m
LEFT JOIN dbo.line_items li ON li.manifest_id = m.id
GROUP BY m.id, m.pallet_number, m.display_name, m.source, m.pallet_reference,
         m.received_date, m.status, m.sell_mode, m.category, m.archived_at,
         m.total_cost, m.photo_url;
GO

GRANT EXECUTE ON dbo.sp_LookupCode TO nsl_api;
GRANT EXECUTE ON dbo.sp_RecordScan TO nsl_api;
GRANT SELECT  ON dbo.v_pallets     TO nsl_api;

PRINT 'Wholesale-price additions applied.';

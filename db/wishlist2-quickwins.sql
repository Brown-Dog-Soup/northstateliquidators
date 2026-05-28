-- ============================================================================
-- Website Backend Wishlist Part 2 — quick-win DB changes.
--
--   #5  Wire `description` through the scan pipeline. lpn_catalog.description
--       and line_items.description already exist (see schema.sql) but nothing
--       carried the value: sp_LookupCode didn't return it, sp_RecordScan didn't
--       persist it. This migration plumbs it end-to-end.
--
--   #6  Fix the Inventory page showing ghost-backstock items as "Sold". In the
--       prior v_inventory the status CASE tested `sold_at` before `is_ghost`,
--       and ghost items have BOTH set — so they always resolved to 'sold' and
--       never 'ghost'. Reorder so is_ghost wins, and ghost items land in the
--       Ghost bucket (and badge) the page already has.
--
-- Rebased on the latest deployed definitions:
--   sp_LookupCode  -> inventory-view-and-dedup.sql  (smart UPC dedup + wholesale)
--   sp_RecordScan  -> wholesale-price-additions.sql (@arg_wholesale_price)
--   v_inventory    -> ghost-pallets.sql             (is_ghost surfaced)
--
-- Apply against sqldb-nsl-prod AFTER all prior db/*.sql migrations.
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- #6  v_inventory — ghost beats sold in the status CASE.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.v_inventory', 'V') IS NOT NULL DROP VIEW dbo.v_inventory;
GO
CREATE VIEW dbo.v_inventory AS
WITH latest_scan AS (
    SELECT
        li.lpn,
        li.id           AS line_item_id,
        li.manifest_id  AS assigned_pallet_id,
        li.qty          AS scanned_qty,
        li.created_at   AS scanned_at,
        li.sold_at,
        ROW_NUMBER() OVER (PARTITION BY li.lpn ORDER BY li.created_at DESC) AS rn
    FROM dbo.line_items li
    WHERE li.lpn IS NOT NULL
)
SELECT
    c.lpn,
    c.upc,
    c.asin,
    c.title,
    c.brand,
    c.category,
    c.subcategory,
    c.condition         AS catalog_condition,
    c.msrp,
    c.unit_cost,
    c.wholesale_price,
    c.qty_in_manifest,
    c.lot_id,
    c.pallet_id         AS source_pallet_id,
    c.order_number,
    c.source_pallet_ref,
    c.source_manifest,
    c.imported_at,
    c.last_seen_at,
    ls.line_item_id,
    ls.assigned_pallet_id,
    m.display_name      AS assigned_pallet_name,
    m.pallet_number     AS assigned_pallet_number,
    m.sell_mode         AS assigned_sell_mode,
    m.archived_at       AS assigned_pallet_archived_at,
    m.is_ghost          AS assigned_pallet_is_ghost,
    ls.scanned_qty,
    ls.scanned_at,
    ls.sold_at,
    CASE
        WHEN ls.line_item_id IS NULL                       THEN 'available'
        WHEN m.is_ghost = 1                                THEN 'ghost'   -- ghost wins over sold_at
        WHEN ls.sold_at IS NOT NULL                        THEN 'sold'
        WHEN m.archived_at IS NOT NULL                     THEN 'archived'
        WHEN m.sell_mode = 'individual'                    THEN 'individual'
        WHEN m.sell_mode IN ('lot','mixed','undecided')    THEN 'on_pallet'
        ELSE 'unknown'
    END AS status
FROM dbo.lpn_catalog c
LEFT JOIN latest_scan ls ON ls.lpn = c.lpn AND ls.rn = 1
LEFT JOIN dbo.manifests m ON m.id = ls.assigned_pallet_id;
GO

GRANT SELECT ON dbo.v_inventory TO nsl_api;

-- ----------------------------------------------------------------------------
-- #5  sp_LookupCode — return description (smart-UPC-dedup version preserved).
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_LookupCode', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_LookupCode;
GO
CREATE PROCEDURE dbo.sp_LookupCode
    @code NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @c NVARCHAR(50) = LTRIM(RTRIM(@code));

    SELECT 'lpn' AS match_source,
        x.lpn, x.asin, x.upc, x.title, x.description, x.brand, x.category, x.subcategory,
        x.msrp, x.unit_cost, x.wholesale_price,
        x.condition, x.qty_in_manifest,
        x.pallet_id, x.lot_id, x.order_number, x.image_url
    FROM (
        SELECT TOP 1
            c.lpn, c.asin, c.upc, c.title, c.description, c.brand, c.category, c.subcategory,
            c.msrp, c.unit_cost, c.wholesale_price,
            c.condition, c.qty_in_manifest,
            c.pallet_id, c.lot_id, c.order_number,
            c.product_image_url AS image_url
        FROM dbo.lpn_catalog c
        WHERE c.lpn = @c
    ) x

    UNION ALL

    -- UPC branch: prefer unassigned rows (no line_items scan yet for this LPN),
    -- ordered by most-recent import so duplicate UPCs get consumed in order.
    SELECT 'upc' AS match_source,
        x.lpn, x.asin, x.upc, x.title, x.description, x.brand, x.category, x.subcategory,
        x.msrp, x.unit_cost, x.wholesale_price,
        x.condition, x.qty_in_manifest,
        x.pallet_id, x.lot_id, x.order_number, x.image_url
    FROM (
        SELECT TOP 1
            c.lpn, c.asin, c.upc, c.title, c.description, c.brand, c.category, c.subcategory,
            c.msrp, c.unit_cost, c.wholesale_price,
            c.condition, c.qty_in_manifest,
            c.pallet_id, c.lot_id, c.order_number,
            c.product_image_url AS image_url
        FROM dbo.lpn_catalog c
        WHERE c.upc = @c
          AND NOT EXISTS (SELECT 1 FROM dbo.lpn_catalog WHERE lpn = @c)
        ORDER BY
            CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.line_items li WHERE li.lpn = c.lpn) THEN 0 ELSE 1 END,
            c.imported_at DESC,
            c.last_seen_at DESC
    ) x

    UNION ALL

    SELECT 'asin' AS match_source,
        x.lpn, x.asin, x.upc, x.title, x.description, x.brand, x.category, x.subcategory,
        x.msrp, x.unit_cost, x.wholesale_price,
        x.condition, x.qty_in_manifest,
        x.pallet_id, x.lot_id, x.order_number, x.image_url
    FROM (
        SELECT TOP 1
            c.lpn, c.asin, c.upc, c.title, c.description, c.brand, c.category, c.subcategory,
            c.msrp, c.unit_cost, c.wholesale_price,
            c.condition, c.qty_in_manifest,
            c.pallet_id, c.lot_id, c.order_number,
            c.product_image_url AS image_url
        FROM dbo.lpn_catalog c
        WHERE c.asin = @c
          AND NOT EXISTS (SELECT 1 FROM dbo.lpn_catalog WHERE lpn = @c OR upc = @c)
        ORDER BY
            CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.line_items li WHERE li.lpn = c.lpn) THEN 0 ELSE 1 END,
            c.imported_at DESC,
            c.last_seen_at DESC
    ) x;
END;
GO

GRANT EXECUTE ON dbo.sp_LookupCode TO nsl_api;

-- ----------------------------------------------------------------------------
-- #5  sp_RecordScan — persist description (wholesale-price version preserved).
--     Catalog description wins; @arg_description fills in for non-catalog hits
--     (e.g. UPCitemdb supplies a description for off-spreadsheet UPCs).
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
    @arg_wholesale_price DECIMAL(12,2) = NULL,
    @arg_description  NVARCHAR(MAX)   = NULL    -- NEW: description from /api/lookup
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
        @title      NVARCHAR(500), @description NVARCHAR(MAX),
        @brand      NVARCHAR(200), @category NVARCHAR(200),
        @msrp       DECIMAL(12,2), @unit_cost DECIMAL(12,4),
        @wholesale  DECIMAL(12,2),
        @cat_cond   VARCHAR(40);

    SELECT TOP 1
        @lpn = c.lpn, @asin = c.asin, @upc = c.upc,
        @title = c.title, @description = c.description,
        @brand = c.brand, @category = c.category,
        @msrp = c.msrp, @unit_cost = c.unit_cost,
        @wholesale = c.wholesale_price,
        @cat_cond = c.condition
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @code OR c.upc = @code OR c.asin = @code;

    DECLARE
        @final_title       NVARCHAR(500) = COALESCE(@title,       @arg_title),
        @final_description NVARCHAR(MAX) = COALESCE(@description, @arg_description),
        @final_brand       NVARCHAR(200) = COALESCE(@brand,       @arg_brand),
        @final_category    NVARCHAR(200) = COALESCE(@category,    @arg_category),
        @final_msrp        DECIMAL(12,2) = COALESCE(@msrp,        @arg_msrp),
        @final_wholesale   DECIMAL(12,2) = COALESCE(@wholesale,   @arg_wholesale_price);

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
         title, description, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
         created_at, enriched_at)
    VALUES
        (@id, @manifest_id, @scan_upc, @scan_lpn, @asin, @qty,
         COALESCE(@condition, @cat_cond),
         @photo_url,
         @enrich_status,
         @enrich_source,
         @final_title, @final_description, @final_brand, @final_category, @final_msrp,
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

GRANT EXECUTE ON dbo.sp_RecordScan TO nsl_api;

-- ----------------------------------------------------------------------------
-- #5  Backfill — populate description on items scanned before this change,
--     pulling from the catalog by lpn / upc / asin. Only fills blanks, so it's
--     safe to re-run.
-- ----------------------------------------------------------------------------
UPDATE li
SET    li.description = c.description
FROM   dbo.line_items li
JOIN   dbo.lpn_catalog c
       ON c.lpn = li.lpn OR c.upc = li.upc OR c.asin = li.asin
WHERE  (li.description IS NULL OR li.description = '')
  AND   c.description IS NOT NULL AND c.description <> '';

PRINT 'Wishlist-2 quick wins applied: description plumbing + backfill (#5) + ghost-vs-sold status fix (#6).';

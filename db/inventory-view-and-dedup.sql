-- ============================================================================
-- Inventory management additions:
--   1. v_inventory view — every catalog row with its assignment status
--      (Available / On Pallet X / Individual / Sold).
--   2. sp_LookupCode UPC branch — when multiple catalog rows share a UPC,
--      prefer one whose LPN has not been scanned yet, so duplicate UPCs
--      get consumed in order rather than always returning the same row.
--
-- Apply against sqldb-nsl-prod.
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- v_inventory: one row per lpn_catalog entry, with the latest line_items
-- assignment (if any) folded in. Powers the /staff/inventory page.
--
-- A catalog row that's been scanned multiple times surfaces the most-recent
-- scan's pallet — the older ones live in line_items history but the headline
-- view shows current state.
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
    ls.scanned_qty,
    ls.scanned_at,
    ls.sold_at,
    CASE
        WHEN ls.line_item_id IS NULL                       THEN 'available'
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
-- sp_LookupCode — when scanning a UPC that maps to multiple catalog rows
-- (same item bought across different lots at different costs), prefer rows
-- whose LPN has not yet been recorded as a line_items scan. Effect: each
-- physical unit consumes its own catalog entry, so cost basis stays right
-- across scans.
--
-- Falls back to most-recent-imported when every duplicate has already been
-- assigned, so you can still scan past the consumed units (the catalog quantity
-- might be higher than what's been received, or vice-versa).
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
        x.lpn, x.asin, x.upc, x.title, x.brand, x.category, x.subcategory,
        x.msrp, x.unit_cost, x.wholesale_price,
        x.condition, x.qty_in_manifest,
        x.pallet_id, x.lot_id, x.order_number, x.image_url
    FROM (
        SELECT TOP 1
            c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
            c.msrp, c.unit_cost, c.wholesale_price,
            c.condition, c.qty_in_manifest,
            c.pallet_id, c.lot_id, c.order_number,
            c.product_image_url AS image_url
        FROM dbo.lpn_catalog c
        WHERE c.lpn = @c
    ) x

    UNION ALL

    -- UPC branch: prefer unassigned rows (no line_items scan yet for this LPN),
    -- ordered by most-recent import so duplicate UPCs get consumed in order
    -- across pallets rather than always returning the same catalog entry.
    SELECT 'upc' AS match_source,
        x.lpn, x.asin, x.upc, x.title, x.brand, x.category, x.subcategory,
        x.msrp, x.unit_cost, x.wholesale_price,
        x.condition, x.qty_in_manifest,
        x.pallet_id, x.lot_id, x.order_number, x.image_url
    FROM (
        SELECT TOP 1
            c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
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
        x.lpn, x.asin, x.upc, x.title, x.brand, x.category, x.subcategory,
        x.msrp, x.unit_cost, x.wholesale_price,
        x.condition, x.qty_in_manifest,
        x.pallet_id, x.lot_id, x.order_number, x.image_url
    FROM (
        SELECT TOP 1
            c.lpn, c.asin, c.upc, c.title, c.brand, c.category, c.subcategory,
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

PRINT 'Inventory view + smart UPC dedup applied.';

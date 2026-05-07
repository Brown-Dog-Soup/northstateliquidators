-- ============================================================================
-- GHOST pallets: a flag on dbo.manifests that marks a pallet as a fake-sold
-- backstock display item. Ghost pallets:
--   - Are built from real lpn_catalog items NSL has owned at some point
--   - Render on the public site with a SOLD badge
--   - Do NOT count toward real available inventory totals
--
-- This keeps the public catalog visually populated with plausible past
-- inventory while the real available pallets are still being built up.
--
-- Apply against sqldb-nsl-prod after inventory-view-and-dedup.sql.
-- ============================================================================

SET NOCOUNT ON;

IF COL_LENGTH('dbo.manifests', 'is_ghost') IS NULL
    ALTER TABLE dbo.manifests ADD is_ghost BIT NOT NULL CONSTRAINT DF_manifests_is_ghost DEFAULT 0;
GO

-- Refresh v_pallets to expose is_ghost so the admin gallery + filters can use it.
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
    m.is_ghost,
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
         m.is_ghost, m.total_cost, m.photo_url;
GO

-- Refresh v_inventory to surface the ghost flag from the assigned pallet,
-- so the inventory page can call out which assignments are ghost vs real.
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
        WHEN ls.sold_at IS NOT NULL                        THEN 'sold'
        WHEN m.archived_at IS NOT NULL                     THEN 'archived'
        WHEN m.is_ghost = 1                                THEN 'ghost'
        WHEN m.sell_mode = 'individual'                    THEN 'individual'
        WHEN m.sell_mode IN ('lot','mixed','undecided')    THEN 'on_pallet'
        ELSE 'unknown'
    END AS status
FROM dbo.lpn_catalog c
LEFT JOIN latest_scan ls ON ls.lpn = c.lpn AND ls.rn = 1
LEFT JOIN dbo.manifests m ON m.id = ls.assigned_pallet_id;
GO

GRANT SELECT ON dbo.v_pallets   TO nsl_api;
GRANT SELECT ON dbo.v_inventory TO nsl_api;

PRINT 'Ghost-pallets flag + view refreshes applied.';

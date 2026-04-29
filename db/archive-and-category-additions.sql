-- ============================================================================
-- Adds archive support + pallet-level category to dbo.manifests, and refreshes
-- v_pallets to expose them. Apply against sqldb-nsl-prod after the base
-- schema, admin-portal-additions.sql, and power-apps-procs.sql.
-- ============================================================================

SET NOCOUNT ON;

-- archived_at: NULL = active, non-NULL = archived (timestamp of when).
-- Storing the timestamp instead of a boolean lets us "undo" an archive cleanly
-- and report on archive activity later if it's useful.
IF COL_LENGTH('dbo.manifests', 'archived_at') IS NULL
    ALTER TABLE dbo.manifests ADD archived_at DATETIME2 NULL;

-- Pallet-level category (Apparel, Electronics, Appliances, Furniture, Home Goods,
-- Holiday, Mixed Goods). The line_items table has its own category column for
-- per-item taxonomy from the manifest; this column is the receiver-applied
-- top-level pallet bucket and is independent of those.
IF COL_LENGTH('dbo.manifests', 'category') IS NULL
    ALTER TABLE dbo.manifests ADD category NVARCHAR(200) NULL;
GO

-- Refresh v_pallets to include the new columns. The admin portal filters by
-- archived_at IS NULL by default; pass ?includeArchived=true to show all.
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
    SUM(li.est_msrp * li.qty)   AS total_msrp,
    SUM(li.est_resale * li.qty) AS total_est_resale,
    SUM(CASE WHEN li.enrich_status = 'hit' THEN 1 ELSE 0 END) AS items_enriched
FROM dbo.manifests m
LEFT JOIN dbo.line_items li ON li.manifest_id = m.id
GROUP BY m.id, m.pallet_number, m.display_name, m.source, m.pallet_reference,
         m.received_date, m.status, m.sell_mode, m.category, m.archived_at,
         m.total_cost, m.photo_url;
GO

GRANT SELECT ON dbo.v_pallets TO nsl_api;
GRANT DELETE ON dbo.line_items TO nsl_api;
GRANT DELETE ON dbo.manifests  TO nsl_api;

PRINT 'Archive + category additions applied.';

-- ============================================================================
-- reset-test-inventory.sql  (one-time cleanup, 2026-06-17)
--
-- Rob is starting the first REAL Bella Canvas boxes and wants a clean slate.
-- All scanned inventory to date was testing. Confirmed scope:
--   * DELETE the 5 draft "real" boxes (incl. TEST, Blue Dolphin, CHRISTMAS x2,
--     AMZ B-Stock) and their ~264 line_items.
--   * DELETE the entire lpn_catalog (2,968 rows from INVENTORY MASTER.xlsx) so
--     the Inventory section is empty for the Bella Canvas CSV upload.
--   * KEEP the 6 ghost backstock pallets (is_ghost = 1) and their line_items —
--     they keep the public storefront's "recently sold" section populated.
--
-- Apply against sqldb-nsl-prod.  IRREVERSIBLE — wrapped in a transaction.
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;

-- enrichment_log FK → line_items; clear any rows tied to non-ghost scans first.
DELETE el
FROM   dbo.enrichment_log el
JOIN   dbo.line_items li ON li.id = el.line_item_id
JOIN   dbo.manifests  m  ON m.id  = li.manifest_id
WHERE  m.is_ghost = 0;

-- 1) test scans on the real (non-ghost) boxes
DELETE li
FROM   dbo.line_items li
JOIN   dbo.manifests m ON m.id = li.manifest_id
WHERE  m.is_ghost = 0;

-- 2) the real (test/draft) boxes themselves
DELETE FROM dbo.manifests WHERE is_ghost = 0;

-- 3) empty the Inventory section (catalog) for the Bella Canvas re-upload
DELETE FROM dbo.lpn_catalog;

COMMIT TRAN;

-- Verify what remains (should be: ghost pallets + their items only; catalog 0).
SELECT 'lpn_catalog'     AS tbl, COUNT(*) AS rows FROM dbo.lpn_catalog
UNION ALL SELECT 'line_items',      COUNT(*) FROM dbo.line_items
UNION ALL SELECT 'manifests_total', COUNT(*) FROM dbo.manifests
UNION ALL SELECT 'manifests_ghost', COUNT(*) FROM dbo.manifests WHERE is_ghost = 1
UNION ALL SELECT 'manifests_real',  COUNT(*) FROM dbo.manifests WHERE is_ghost = 0;

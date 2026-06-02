-- ============================================================================
-- Fix: sp_CreatePalletFromCatalog dropped quantity to 1.
--
-- The "build a pallet from selected inventory" path (#9) inserted each catalog
-- row as a single qty=1 line item, ignoring c.qty_in_manifest. So a lot row for
-- "3 headphones" landed on the pallet as qty 1 — the other two vanished, and the
-- public View Manifest popup showed qty 1.
--
-- This redefines the proc to carry COALESCE(qty_in_manifest, 1) onto the line
-- item. Idempotent. Apply against sqldb-nsl-prod AFTER wishlist2-part2.sql.
-- ============================================================================

SET NOCOUNT ON;

IF OBJECT_ID('dbo.sp_CreatePalletFromCatalog', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_CreatePalletFromCatalog;
GO
CREATE PROCEDURE dbo.sp_CreatePalletFromCatalog
    @display_name NVARCHAR(200) = NULL,
    @lpns_json    NVARCHAR(MAX)          -- JSON array of lpn strings, e.g. '["LPN1","LPN2"]'
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @id  UNIQUEIDENTIFIER = NEWID();
    DECLARE @num INT = NEXT VALUE FOR dbo.seq_pallet_number;
    DECLARE @name NVARCHAR(200) =
        COALESCE(NULLIF(LTRIM(RTRIM(@display_name)), ''),
                 'Pallet #' + RIGHT('000' + CAST(@num AS VARCHAR(10)), 3));

    INSERT INTO dbo.manifests
        (id, source, status, sell_mode, publish_state,
         display_name, pallet_number, notes)
    VALUES
        (@id, 'inventory-build', 'receiving', 'undecided', 'draft',
         @name, @num, 'Built from selected inventory items.');

    -- Selected catalog rows → line items on the new pallet. qty now follows the
    -- manifest count (qty_in_manifest) instead of being forced to 1, so a lot row
    -- for "3 headphones" arrives on the pallet as qty 3.
    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, description, brand, category,
         est_msrp, est_resale, unit_cost, wholesale_price,
         created_at, enriched_at)
    SELECT
        NEWID(), @id, c.upc, c.lpn, c.asin, COALESCE(c.qty_in_manifest, 1),
        COALESCE(c.condition, 'untested'),
        c.product_image_url, 'hit', 'lpn_catalog',
        c.title, c.description, c.brand, c.category,
        c.msrp, NULL, c.unit_cost, c.wholesale_price,
        SYSUTCDATETIME(), SYSUTCDATETIME()
    FROM dbo.lpn_catalog c
    JOIN OPENJSON(@lpns_json) j ON j.value = c.lpn;

    DECLARE @count INT = @@ROWCOUNT;
    SELECT @id AS id, @num AS pallet_number, @name AS display_name, @count AS items_added;
END;
GO
GRANT EXECUTE ON dbo.sp_CreatePalletFromCatalog TO nsl_api;

PRINT 'sp_CreatePalletFromCatalog: qty now follows qty_in_manifest (was hardcoded 1).';

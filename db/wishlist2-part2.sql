-- ============================================================================
-- Website Backend Wishlist Part 2 — remaining items (DB layer).
--
--   #6  publish_state on pallets (draft | live | ghost | sold) + transition
--       proc sp_SetPublishState that keeps is_ghost / sold_at / line_items
--       in sync, and v_public_pallets for the public site read path.
--   #3  list_price + sale_price on pallets (publish-time override + strike-
--       through sale price).
--   #9  sp_CreatePalletFromCatalog — build a pallet from chosen catalog rows
--       (the Inventory "check items → make a pallet" path, for barcode-less
--       goods like Bella Canvas shirts).
--
-- Master/derived rule: publish_state is the source of truth. is_ghost and
-- sold_at are kept consistent with it so the existing v_pallets / v_inventory
-- ghost-vs-sold logic (and the ghost backstock generator) keep working.
--
-- Idempotent. Apply against sqldb-nsl-prod AFTER all prior db/*.sql migrations
-- (notably wishlist2-quickwins.sql).
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- #6 / #3  manifests — new columns
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.manifests', 'publish_state') IS NULL
    ALTER TABLE dbo.manifests
        ADD publish_state VARCHAR(20) NOT NULL
            CONSTRAINT DF_manifests_publish_state DEFAULT 'draft';
GO
IF COL_LENGTH('dbo.manifests', 'list_price') IS NULL
    ALTER TABLE dbo.manifests ADD list_price DECIMAL(12,2) NULL;
IF COL_LENGTH('dbo.manifests', 'sale_price') IS NULL
    ALTER TABLE dbo.manifests ADD sale_price DECIMAL(12,2) NULL;
GO

-- Backfill publish_state from the legacy is_ghost flag. Existing ghost
-- backstock → 'ghost'; everything else stays 'draft' (nothing is published to
-- the website yet, so draft is the safe baseline). Only touch rows still at the
-- default so re-runs don't stomp later manual changes.
UPDATE dbo.manifests
SET    publish_state = 'ghost'
WHERE  is_ghost = 1 AND publish_state = 'draft';
GO

-- Add the CHECK after backfill so existing data can't trip it.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_manifests_publish_state')
    ALTER TABLE dbo.manifests
        ADD CONSTRAINT CK_manifests_publish_state
        CHECK (publish_state IN ('draft','live','ghost','sold'));
GO

-- ----------------------------------------------------------------------------
-- #6  sp_SetPublishState — single source of truth for publish transitions.
--     Keeps is_ghost / manifests.sold_at / line_items.sold_at consistent.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_SetPublishState', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_SetPublishState;
GO
CREATE PROCEDURE dbo.sp_SetPublishState
    @manifest_id   UNIQUEIDENTIFIER,
    @publish_state VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    IF @publish_state NOT IN ('draft','live','ghost','sold')
    BEGIN
        RAISERROR('publish_state must be one of draft | live | ghost | sold.', 16, 1);
        RETURN;
    END;

    BEGIN TRAN;

    UPDATE dbo.manifests
    SET publish_state = @publish_state,
        is_ghost = CASE WHEN @publish_state = 'ghost' THEN 1 ELSE 0 END,
        sold_at  = CASE
                       WHEN @publish_state IN ('ghost','sold') THEN COALESCE(sold_at, SYSUTCDATETIME())
                       ELSE NULL          -- live / draft clear the sold timestamp
                   END,
        updated_at = SYSUTCDATETIME()
    WHERE id = @manifest_id;

    -- Item-level inventory effect:
    --   sold        → items leave inventory (stamp sold_at)
    --   live/draft  → items are back in play (clear sold_at)
    --   ghost       → leave items as-is (ghost is fake; is_ghost wins in v_inventory)
    IF @publish_state = 'sold'
        UPDATE dbo.line_items
        SET sold_at = COALESCE(sold_at, SYSUTCDATETIME())
        WHERE manifest_id = @manifest_id;
    ELSE IF @publish_state IN ('live','draft')
        UPDATE dbo.line_items
        SET sold_at = NULL
        WHERE manifest_id = @manifest_id;

    COMMIT TRAN;

    SELECT id, publish_state, is_ghost, sold_at, status
    FROM dbo.manifests WHERE id = @manifest_id;
END;
GO
GRANT EXECUTE ON dbo.sp_SetPublishState TO nsl_api;

-- ----------------------------------------------------------------------------
-- #6 / #3  v_pallets — expose publish_state + list/sale price (rebased on the
--          ghost-backstock-generator definition, which is the deployed one).
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
    m.sold_at,
    m.status,
    m.sell_mode,
    m.publish_state,
    m.list_price,
    m.sale_price,
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
         m.received_date, m.sold_at, m.status, m.sell_mode, m.publish_state,
         m.list_price, m.sale_price, m.category, m.archived_at, m.is_ghost,
         m.total_cost, m.photo_url;
GO
GRANT SELECT ON dbo.v_pallets TO nsl_api;

-- ----------------------------------------------------------------------------
-- #6  v_public_pallets — what the public website is allowed to show.
--     Live pallets (for sale) + ghost/sold (shown as SOLD social proof).
--     ask_price = sale_price ?? list_price ?? rolled-up wholesale total.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.v_public_pallets', 'V') IS NOT NULL DROP VIEW dbo.v_public_pallets;
GO
CREATE VIEW dbo.v_public_pallets AS
SELECT
    p.manifest_id,
    p.pallet_number,
    p.display_name,
    p.category,
    p.publish_state,
    p.received_date,
    p.sold_at,
    p.photo_url,
    p.item_count,
    p.unit_count,
    p.total_msrp,
    p.list_price,
    p.sale_price,
    CAST(CASE WHEN p.publish_state IN ('ghost','sold') THEN 1 ELSE 0 END AS BIT) AS is_sold,
    CAST(CASE WHEN p.sale_price IS NOT NULL AND p.list_price IS NOT NULL
                   AND p.sale_price < p.list_price
              THEN 1 ELSE 0 END AS BIT) AS is_on_sale,
    COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p
WHERE p.archived_at IS NULL
  AND p.publish_state IN ('live','ghost','sold');
GO
GRANT SELECT ON dbo.v_public_pallets TO nsl_api;

-- ----------------------------------------------------------------------------
-- #9  sp_CreatePalletFromCatalog — make a pallet from a set of catalog LPNs
--     (the Inventory "check items → make a pallet" action). Mirrors how
--     sp_RecordScan seeds a line item from lpn_catalog, but in bulk and without
--     a physical scan. Used for barcode-less goods checked off in Inventory.
-- ----------------------------------------------------------------------------
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

    -- Selected catalog rows → line items on the new pallet.
    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, description, brand, category,
         est_msrp, est_resale, unit_cost, wholesale_price,
         created_at, enriched_at)
    SELECT
        NEWID(), @id, c.upc, c.lpn, c.asin, 1,
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

PRINT 'Wishlist-2 Part 2 DB layer applied: publish_state + prices (#6/#3), sp_SetPublishState, v_public_pallets, sp_CreatePalletFromCatalog (#9).';

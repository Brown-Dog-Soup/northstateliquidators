-- ============================================================================
-- box-quantities.sql  (2026-06-17)
--
-- Rob's ask #2 / #3: put a SPECIFIC QUANTITY of an inventory item into a box
-- (split "10 red sweatshirts" → 5 in this box, 5 still available), and delete an
-- inventory item that isn't on a pallet yet.
--
-- Model: lpn_catalog.qty_in_manifest is the TOTAL units on hand for that SKU.
-- Allocations are tracked purely as line items, so:
--       available = qty_in_manifest - SUM(line_items.qty for that lpn)
-- Allocating K units drops a qty=K line item on a box; pulling an item back out
-- (deleting that line item) returns its units to "available" automatically — no
-- counter to keep in sync, so units can never be stranded.
--
-- Safe to apply against sqldb-nsl-prod: the catalog is empty post-reset, so
-- there is no historical data to backfill. Idempotent (DROP/CREATE).
--
-- NOTE: this SUPERSEDES db/pallet-from-catalog-qty.sql — it redefines
-- sp_CreatePalletFromCatalog again (full remaining qty per checked item, under
-- the derived-availability model). Apply this one last.
-- ============================================================================
SET NOCOUNT ON;
GO

-- ----------------------------------------------------------------------------
-- v_inventory — quantity-aware. Adds available_qty + allocated_qty; status is
-- driven by the remaining pool, not just "does a line item exist".
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
),
alloc AS (   -- total units of each lpn already placed into boxes
    SELECT lpn, SUM(qty) AS allocated_qty
    FROM dbo.line_items
    WHERE lpn IS NOT NULL
    GROUP BY lpn
)
SELECT
    c.lpn, c.upc, c.asin, c.title, c.brand, c.category, c.subcategory,
    c.condition         AS catalog_condition,
    c.msrp, c.unit_cost, c.wholesale_price,
    c.qty_in_manifest,                                 -- total units on hand
    CASE WHEN ISNULL(c.qty_in_manifest, 0) - ISNULL(a.allocated_qty, 0) > 0
         THEN ISNULL(c.qty_in_manifest, 0) - ISNULL(a.allocated_qty, 0)
         ELSE 0 END               AS available_qty,    -- total - already in boxes
    ISNULL(a.allocated_qty, 0)    AS allocated_qty,    -- units already in boxes
    c.lot_id, c.pallet_id AS source_pallet_id, c.order_number,
    c.source_pallet_ref, c.source_manifest, c.imported_at, c.last_seen_at,
    ls.line_item_id, ls.assigned_pallet_id,
    m.display_name      AS assigned_pallet_name,
    m.pallet_number     AS assigned_pallet_number,
    m.sell_mode         AS assigned_sell_mode,
    m.archived_at       AS assigned_pallet_archived_at,
    m.is_ghost          AS assigned_pallet_is_ghost,
    ls.scanned_qty, ls.scanned_at, ls.sold_at,
    CASE
        WHEN ISNULL(c.qty_in_manifest, 0) - ISNULL(a.allocated_qty, 0) > 0 THEN 'available'
        WHEN ls.line_item_id IS NULL           THEN 'available'   -- nothing placed (edge: qty 0)
        WHEN m.is_ghost = 1                    THEN 'ghost'
        WHEN ls.sold_at IS NOT NULL            THEN 'sold'
        WHEN m.archived_at IS NOT NULL         THEN 'archived'
        ELSE 'allocated'                                          -- every unit is in a box
    END AS status
FROM dbo.lpn_catalog c
LEFT JOIN latest_scan ls ON ls.lpn = c.lpn AND ls.rn = 1
LEFT JOIN alloc a        ON a.lpn  = c.lpn
LEFT JOIN dbo.manifests m ON m.id  = ls.assigned_pallet_id;
GO
GRANT SELECT ON dbo.v_inventory TO nsl_api;
GO

-- ----------------------------------------------------------------------------
-- sp_AllocateCatalogToBox — put @qty units of @lpn into a box. Creates the box
-- when @manifest_id is NULL. Decrements the available pool. The whole thing is
-- transactional so a half-allocation can't leak the pool.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_AllocateCatalogToBox', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_AllocateCatalogToBox;
GO
CREATE PROCEDURE dbo.sp_AllocateCatalogToBox
    @lpn          VARCHAR(40),
    @qty          INT,
    @manifest_id  UNIQUEIDENTIFIER = NULL,   -- existing box; NULL = create a new one
    @new_box_name NVARCHAR(200)   = NULL     -- name to use when creating a new box
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @qty IS NULL OR @qty < 1
    BEGIN RAISERROR('Quantity must be a positive whole number.', 16, 1); RETURN; END;

    DECLARE @total INT;
    SELECT @total = ISNULL(qty_in_manifest, 0) FROM dbo.lpn_catalog WHERE lpn = @lpn;
    IF @@ROWCOUNT = 0
    BEGIN RAISERROR('No inventory item with that code.', 16, 1); RETURN; END;

    -- available = total on hand - everything already placed in boxes
    DECLARE @allocated INT =
        (SELECT ISNULL(SUM(qty), 0) FROM dbo.line_items WHERE lpn = @lpn);
    DECLARE @avail INT = @total - @allocated;
    IF @qty > @avail
    BEGIN RAISERROR('Only %d unit(s) available to add.', 16, 1, @avail); RETURN; END;

    BEGIN TRAN;

    -- Create the box if the caller didn't pick an existing one.
    IF @manifest_id IS NULL
    BEGIN
        DECLARE @num  INT = NEXT VALUE FOR dbo.seq_pallet_number;
        SET @manifest_id = NEWID();
        DECLARE @name NVARCHAR(200) =
            COALESCE(NULLIF(LTRIM(RTRIM(@new_box_name)), ''),
                     'Box #' + RIGHT('000' + CAST(@num AS VARCHAR(10)), 3));
        INSERT INTO dbo.manifests
            (id, source, status, sell_mode, publish_state, display_name, pallet_number, notes)
        VALUES
            (@manifest_id, 'inventory-build', 'receiving', 'undecided', 'draft',
             @name, @num, 'Built from inventory allocations.');
    END
    ELSE IF NOT EXISTS (SELECT 1 FROM dbo.manifests WHERE id = @manifest_id)
    BEGIN
        ROLLBACK TRAN;
        RAISERROR('Target box not found.', 16, 1); RETURN;
    END;

    -- One qty=@qty line item on the box, copying catalog fields (mirrors
    -- sp_CreatePalletFromCatalog so the box detail page renders it the same way).
    DECLARE @line_id UNIQUEIDENTIFIER = NEWID();
    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, description, brand, category,
         est_msrp, est_resale, unit_cost, wholesale_price,
         created_at, enriched_at)
    SELECT
        @line_id, @manifest_id, c.upc, c.lpn, c.asin, @qty,
        COALESCE(c.condition, 'untested'),
        c.product_image_url, 'hit', 'inventory-alloc',
        c.title, c.description, c.brand, c.category,
        c.msrp, NULL, c.unit_cost, c.wholesale_price,
        SYSUTCDATETIME(), SYSUTCDATETIME()
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @lpn;

    -- Note: qty_in_manifest (total on hand) is intentionally NOT changed —
    -- availability is derived as total - SUM(line_items.qty), so removing this
    -- item from the box later restores its units automatically.
    UPDATE dbo.lpn_catalog SET last_seen_at = SYSUTCDATETIME() WHERE lpn = @lpn;

    COMMIT TRAN;

    SELECT m.id AS manifest_id, m.pallet_number, m.display_name,
           @qty AS allocated, (@avail - @qty) AS remaining, @line_id AS line_item_id
    FROM dbo.manifests m WHERE m.id = @manifest_id;
END;
GO
GRANT EXECUTE ON dbo.sp_AllocateCatalogToBox TO nsl_api;
GO

-- ----------------------------------------------------------------------------
-- sp_CreatePalletFromCatalog — keep the #9 "check items → build a box" flow
-- consistent with the new pool model: each checked item moves its FULL remaining
-- quantity into the box, and its pool is zeroed.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_CreatePalletFromCatalog', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_CreatePalletFromCatalog;
GO
CREATE PROCEDURE dbo.sp_CreatePalletFromCatalog
    @display_name NVARCHAR(200) = NULL,
    @lpns_json    NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @id  UNIQUEIDENTIFIER = NEWID();
    DECLARE @num INT = NEXT VALUE FOR dbo.seq_pallet_number;
    DECLARE @name NVARCHAR(200) =
        COALESCE(NULLIF(LTRIM(RTRIM(@display_name)), ''),
                 'Box #' + RIGHT('000' + CAST(@num AS VARCHAR(10)), 3));

    BEGIN TRAN;

    INSERT INTO dbo.manifests
        (id, source, status, sell_mode, publish_state, display_name, pallet_number, notes)
    VALUES
        (@id, 'inventory-build', 'receiving', 'undecided', 'draft',
         @name, @num, 'Built from selected inventory items.');

    -- Each checked catalog row → one line item carrying its full remaining qty.
    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition,
         photo_blob_url, enrich_status, enrich_source,
         title, description, brand, category,
         est_msrp, est_resale, unit_cost, wholesale_price,
         created_at, enriched_at)
    SELECT
        NEWID(), @id, c.upc, c.lpn, c.asin,
        rem.remaining,
        COALESCE(c.condition, 'untested'),
        c.product_image_url, 'hit', 'lpn_catalog',
        c.title, c.description, c.brand, c.category,
        c.msrp, NULL, c.unit_cost, c.wholesale_price,
        SYSUTCDATETIME(), SYSUTCDATETIME()
    FROM dbo.lpn_catalog c
    JOIN OPENJSON(@lpns_json) j ON j.value = c.lpn
    CROSS APPLY (
        -- full remaining availability (total - already boxed); at least 1 since
        -- the user explicitly checked this item to box it.
        SELECT CASE WHEN ISNULL(c.qty_in_manifest, 0)
                         - ISNULL((SELECT SUM(qty) FROM dbo.line_items li WHERE li.lpn = c.lpn), 0) > 1
                    THEN ISNULL(c.qty_in_manifest, 0)
                         - ISNULL((SELECT SUM(qty) FROM dbo.line_items li WHERE li.lpn = c.lpn), 0)
                    ELSE 1 END AS remaining
    ) rem;

    DECLARE @count INT = @@ROWCOUNT;

    -- qty_in_manifest (total on hand) stays put; availability is derived.
    UPDATE c SET last_seen_at = SYSUTCDATETIME()
    FROM dbo.lpn_catalog c
    JOIN OPENJSON(@lpns_json) j ON j.value = c.lpn;

    COMMIT TRAN;

    SELECT @id AS id, @num AS pallet_number, @name AS display_name, @count AS items_added;
END;
GO
GRANT EXECUTE ON dbo.sp_CreatePalletFromCatalog TO nsl_api;
GO

PRINT 'box-quantities.sql applied: qty-aware v_inventory + sp_AllocateCatalogToBox + sp_CreatePalletFromCatalog (pool model).';

-- ----------------------------------------------------------------------------
-- Bridged-scan fidelity (July 2026)
--
-- Context: /api/lookup can now "bridge" a scanned retail UPC to a catalog row
-- via the ASIN/EAN the external UPC provider returns (the catalog row itself
-- has a blank upc). Two gaps this migration closes:
--
--   1. sp_RecordScan re-probed the catalog by the RAW scanned code only, so a
--      bridged lookup that displayed manifest cost/price on the scan card
--      recorded a line item with NO unit_cost/wholesale_price. New optional
--      @arg_lpn carries the exact catalog row identity the lookup matched.
--      Also fixes nondeterministic row choice when duplicate UPCs exist —
--      the client now names the row instead of TOP 1 without ORDER BY.
--
--   2. sp_LearnUpc — self-healing catalog. On a bridged hit, the API backfills
--      the scanned UPC onto the matched catalog row (NULL-fill only, never
--      overwrites). Subsequent scans of the same product match the catalog
--      directly without burning the UPC provider's 100/day trial quota.
--
-- sp_RecordScan body is otherwise the wishlist2-quickwins version (description
-- persistence preserved). @arg_lpn is optional — the deployed API keeps working
-- until the new build ships.
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
    @arg_description  NVARCHAR(MAX)   = NULL,
    @arg_lpn          VARCHAR(40)     = NULL    -- NEW: catalog LPN the lookup matched (bridged or direct)
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

    -- Exact row named by the lookup wins; raw-code probe is the fallback for
    -- callers that don't pass @arg_lpn (older UI, Power Apps).
    SELECT TOP 1
        @lpn = c.lpn, @asin = c.asin, @upc = c.upc,
        @title = c.title, @description = c.description,
        @brand = c.brand, @category = c.category,
        @msrp = c.msrp, @unit_cost = c.unit_cost,
        @wholesale = c.wholesale_price,
        @cat_cond = c.condition
    FROM dbo.lpn_catalog c
    WHERE c.lpn = @arg_lpn;

    IF @lpn IS NULL
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
    -- 'LP[A-Z0-9]%' (not 'LPN%') so Target LPTG/LPHZ/LPJW stickers count as
    -- LPN-shaped codes rather than falling into the upc column on a miss.
    DECLARE @scan_lpn VARCHAR(40) = CASE WHEN @code LIKE 'LP[A-Z0-9]%' OR @lpn IS NOT NULL THEN COALESCE(@lpn, @code) END;
    -- Keep the physically scanned barcode when it was a UPC — even on a bridged
    -- hit where the catalog row's own upc is blank.
    DECLARE @scan_upc VARCHAR(20) = CASE WHEN @code NOT LIKE 'LP[A-Z0-9]%' THEN LEFT(@code, 20) ELSE @upc END;

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
GO

-- ----------------------------------------------------------------------------
-- sp_LearnUpc — NULL-fill a catalog row's upc from a bridged scan. Guarded to
-- GTIN-shaped codes; never overwrites an existing upc.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_LearnUpc', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_LearnUpc;
GO
CREATE PROCEDURE dbo.sp_LearnUpc
    @lpn VARCHAR(40),
    @upc VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF @lpn IS NULL OR @upc IS NULL RETURN;
    IF @upc NOT LIKE REPLICATE('[0-9]', 12) AND @upc NOT LIKE REPLICATE('[0-9]', 13) RETURN;

    UPDATE dbo.lpn_catalog
    SET upc = @upc,
        last_seen_at = SYSUTCDATETIME()
    WHERE lpn = @lpn
      AND NULLIF(upc, '') IS NULL;
END;
GO

GRANT EXECUTE ON dbo.sp_LearnUpc TO nsl_api;
GO

PRINT 'Bridged-scan fidelity applied: sp_RecordScan @arg_lpn + sp_LearnUpc.';

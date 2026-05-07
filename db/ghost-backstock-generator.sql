-- ============================================================================
-- Ghost backstock: a "sold pallets history" you can generate on demand from
-- real catalog items with past sold dates. Makes the public catalog look like
-- NSL has been moving inventory for months. Items chosen are real (from
-- lpn_catalog), but the pallets themselves never represent physical stock —
-- they exist only for storefront social-proof.
--
-- Adds:
--   - manifests.sold_at — when a pallet was sold (real or ghost)
--   - sp_GenerateGhostBackstock — picks random catalog items, builds N ghost
--     pallets with past received + sold dates
--
-- Apply against sqldb-nsl-prod after ghost-pallets.sql.
-- ============================================================================

SET NOCOUNT ON;

IF COL_LENGTH('dbo.manifests', 'sold_at') IS NULL
    ALTER TABLE dbo.manifests ADD sold_at DATETIME2 NULL;
GO

-- Refresh v_pallets one more time to expose sold_at.
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
         m.received_date, m.sold_at, m.status, m.sell_mode, m.category,
         m.archived_at, m.is_ghost, m.total_cost, m.photo_url;
GO

-- ----------------------------------------------------------------------------
-- sp_GenerateGhostBackstock
--
-- Creates @pallet_count ghost pallets, each containing @items_per_pallet
-- random catalog items, with received_date 7-180 days ago and sold_at
-- 1-30 days after received. Categories and sell_mode are randomized to
-- match what real-world inventory looks like.
--
-- Items are picked from lpn_catalog rows that already have title + msrp +
-- unit_cost + wholesale_price (full data). Same item can appear across
-- multiple ghost pallets — these are imaginary sales, not consumption.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_GenerateGhostBackstock', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_GenerateGhostBackstock;
GO
CREATE PROCEDURE dbo.sp_GenerateGhostBackstock
    @pallet_count     INT = 5,
    @items_per_pallet INT = 12
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @categories TABLE (n NVARCHAR(50));
    INSERT INTO @categories(n) VALUES
        ('Apparel'),('Electronics'),('Appliances'),('Furniture'),
        ('Home Goods'),('Holiday'),('Mixed Goods');

    DECLARE @modes TABLE (m VARCHAR(20));
    INSERT INTO @modes(m) VALUES ('lot'),('individual'),('mixed');

    DECLARE @created TABLE (id UNIQUEIDENTIFIER, display_name NVARCHAR(200), sold_at DATETIME2);

    DECLARE @i INT = 0;
    WHILE @i < @pallet_count
    BEGIN
        SET @i = @i + 1;

        DECLARE @id UNIQUEIDENTIFIER = NEWID();
        DECLARE @num INT = NEXT VALUE FOR dbo.seq_pallet_number;
        DECLARE @daysAgoSold INT = 7 + ABS(CHECKSUM(NEWID())) % 174;     -- 7..180
        DECLARE @daysAgoReceived INT = @daysAgoSold + 1 + ABS(CHECKSUM(NEWID())) % 29;  -- 1..30 days before sold
        DECLARE @soldAt DATETIME2 = DATEADD(DAY, -@daysAgoSold, SYSUTCDATETIME());
        DECLARE @recvAt DATETIME2 = DATEADD(DAY, -@daysAgoReceived, SYSUTCDATETIME());
        DECLARE @category NVARCHAR(50) = (SELECT TOP 1 n FROM @categories ORDER BY NEWID());
        DECLARE @mode VARCHAR(20)      = (SELECT TOP 1 m FROM @modes      ORDER BY NEWID());
        DECLARE @name NVARCHAR(200) =
            @category + ' Pallet · ' + FORMAT(@soldAt, 'MMM yyyy') + ' #' + CAST(@num AS VARCHAR(10));

        INSERT INTO dbo.manifests
            (id, source, pallet_reference, status, sell_mode,
             display_name, pallet_number, notes,
             received_date, sold_at, category, is_ghost)
        VALUES
            (@id, 'ghost-backstock', NULL, 'sold', @mode,
             @name, @num, 'Auto-generated ghost backstock for storefront display.',
             @recvAt, @soldAt, @category, 1);

        -- Pick @items_per_pallet random catalog rows with complete pricing data.
        ;WITH picks AS (
            SELECT TOP (@items_per_pallet)
                c.lpn, c.upc, c.asin, c.title, c.brand, c.category,
                c.msrp, c.unit_cost, c.wholesale_price, c.condition,
                c.product_image_url
            FROM dbo.lpn_catalog c
            WHERE c.title IS NOT NULL
              AND c.msrp > 0 AND c.unit_cost > 0 AND c.wholesale_price > 0
            ORDER BY NEWID()
        )
        INSERT INTO dbo.line_items
            (id, manifest_id, upc, lpn, asin, qty, condition,
             photo_blob_url, enrich_status, enrich_source,
             title, brand, category,
             est_msrp, est_resale, unit_cost, wholesale_price,
             created_at, enriched_at, sold_at)
        SELECT
            NEWID(), @id, p.upc, p.lpn, p.asin, 1,
            COALESCE(p.condition, 'open_box'),
            p.product_image_url, 'hit', 'lpn_catalog',
            p.title, p.brand, p.category,
            p.msrp, p.wholesale_price, p.unit_cost, p.wholesale_price,
            @recvAt, @recvAt, @soldAt
        FROM picks p;

        INSERT INTO @created (id, display_name, sold_at)
        VALUES (@id, @name, @soldAt);
    END;

    SELECT id AS manifest_id, display_name, sold_at FROM @created ORDER BY sold_at DESC;
END;
GO

GRANT EXECUTE ON dbo.sp_GenerateGhostBackstock TO nsl_api;

PRINT 'Ghost backstock generator applied.';

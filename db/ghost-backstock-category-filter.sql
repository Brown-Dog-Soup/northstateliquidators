-- ============================================================================
-- Fix sp_GenerateGhostBackstock so the items inside a generated pallet
-- actually match the pallet's category. Previously the pallet name (e.g.
-- "Holiday Pallet") was random from the 7 NSL categories, but the items
-- were drawn from the whole catalog, so a Holiday pallet might contain
-- swimwear and electronics. This version maps each NSL category to a set
-- of supplier-category keywords and filters lpn_catalog accordingly.
--
-- Falls back to "Mixed Goods" with an unfiltered pick when the chosen
-- category doesn't have enough matching catalog rows. Also cleans up any
-- previously-generated ghost pallets created by the broken version.
--
-- Apply against sqldb-nsl-prod after ghost-backstock-generator.sql.
-- ============================================================================

SET NOCOUNT ON;

-- Wipe the bad ghost pallets that were generated before this fix. Real
-- pallets are not touched.
DELETE FROM dbo.line_items
WHERE manifest_id IN (SELECT id FROM dbo.manifests WHERE is_ghost = 1);
DELETE FROM dbo.manifests WHERE is_ghost = 1;
GO

-- ----------------------------------------------------------------------------
-- sp_GenerateGhostBackstock — category-aware version.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_GenerateGhostBackstock', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_GenerateGhostBackstock;
GO
CREATE PROCEDURE dbo.sp_GenerateGhostBackstock
    @pallet_count     INT = 5,
    @items_per_pallet INT = 12
AS
BEGIN
    SET NOCOUNT ON;

    -- Map NSL public categories -> supplier-category keywords. The supplier
    -- (lpn_catalog.category) ships specific labels like "Women's Swimwear",
    -- "TRIM A TREE", "Apparel", etc. Each NSL bucket gets a list of LIKE
    -- patterns that catch the relevant rows.
    DECLARE @cat_map TABLE (nsl_category NVARCHAR(50), keyword NVARCHAR(80));
    INSERT INTO @cat_map(nsl_category, keyword) VALUES
        ('Apparel','apparel'),('Apparel','women'),('Apparel','men'),('Apparel','girls'),
        ('Apparel','boys'),('Apparel','baby apparel'),('Apparel','swim'),('Apparel','outerwear'),
        ('Apparel','denim'),('Apparel','tops'),('Apparel','dresses'),('Apparel','intimate'),
        ('Apparel','hosiery'),('Apparel','shapewear'),('Apparel','athletic'),('Apparel','sleep'),
        ('Apparel','underwear'),('Apparel','sportswear'),('Apparel','accessories'),
        ('Apparel','contemporary'),('Apparel','maternity'),
        ('Holiday','trim a tree'),('Holiday','holiday'),('Holiday','christmas'),
        ('Holiday','seasonal'),('Holiday','halloween'),('Holiday','easter'),
        ('Electronics','electronic'),('Electronics','audio'),('Electronics','headphone'),
        ('Electronics','speaker'),('Electronics','tv'),('Electronics','phone'),
        ('Electronics','tablet'),('Electronics','laptop'),
        ('Appliances','appliance'),('Appliances','kitchen'),('Appliances','coffee'),
        ('Appliances','blender'),('Appliances','vacuum'),
        ('Furniture','furniture'),('Furniture','chair'),('Furniture','table'),
        ('Furniture','desk'),('Furniture','sofa'),('Furniture','couch'),
        ('Home Goods','home'),('Home Goods','bath'),('Home Goods','bedding'),
        ('Home Goods','decor'),('Home Goods','linen');

    -- The 7 NSL categories (Mixed Goods always succeeds via fallback).
    DECLARE @categories TABLE (n NVARCHAR(50));
    INSERT INTO @categories(n) VALUES
        ('Apparel'),('Electronics'),('Appliances'),('Furniture'),
        ('Home Goods'),('Holiday'),('Mixed Goods');

    DECLARE @modes TABLE (m VARCHAR(20));
    INSERT INTO @modes(m) VALUES ('lot'),('individual'),('mixed');

    DECLARE @created TABLE (id UNIQUEIDENTIFIER, display_name NVARCHAR(200), sold_at DATETIME2, item_count INT);

    DECLARE @i INT = 0;
    WHILE @i < @pallet_count
    BEGIN
        SET @i = @i + 1;

        DECLARE @id UNIQUEIDENTIFIER = NEWID();
        DECLARE @num INT = NEXT VALUE FOR dbo.seq_pallet_number;
        DECLARE @daysAgoSold INT = 7 + ABS(CHECKSUM(NEWID())) % 174;
        DECLARE @daysAgoReceived INT = @daysAgoSold + 1 + ABS(CHECKSUM(NEWID())) % 29;
        DECLARE @soldAt DATETIME2 = DATEADD(DAY, -@daysAgoSold, SYSUTCDATETIME());
        DECLARE @recvAt DATETIME2 = DATEADD(DAY, -@daysAgoReceived, SYSUTCDATETIME());
        DECLARE @category NVARCHAR(50) = (SELECT TOP 1 n FROM @categories ORDER BY NEWID());
        DECLARE @mode VARCHAR(20)      = (SELECT TOP 1 m FROM @modes      ORDER BY NEWID());

        -- Stage candidate item picks for this pallet, filtered to the category's
        -- keyword list. Mixed Goods skips the filter (catch-all).
        DECLARE @picks TABLE (
            lpn varchar(40), upc varchar(20), asin varchar(20),
            title nvarchar(500), brand nvarchar(200), category nvarchar(200),
            msrp decimal(12,2), unit_cost decimal(12,4), wholesale_price decimal(12,2),
            condition varchar(40), product_image_url nvarchar(1000)
        );
        DELETE FROM @picks;

        IF @category = 'Mixed Goods'
        BEGIN
            INSERT INTO @picks
            SELECT TOP (@items_per_pallet)
                c.lpn, c.upc, c.asin, c.title, c.brand, c.category,
                c.msrp, c.unit_cost, c.wholesale_price, c.condition, c.product_image_url
            FROM dbo.lpn_catalog c
            WHERE c.title IS NOT NULL
              AND c.msrp > 0 AND c.unit_cost > 0 AND c.wholesale_price > 0
            ORDER BY NEWID();
        END
        ELSE
        BEGIN
            INSERT INTO @picks
            SELECT TOP (@items_per_pallet)
                c.lpn, c.upc, c.asin, c.title, c.brand, c.category,
                c.msrp, c.unit_cost, c.wholesale_price, c.condition, c.product_image_url
            FROM dbo.lpn_catalog c
            WHERE c.title IS NOT NULL
              AND c.msrp > 0 AND c.unit_cost > 0 AND c.wholesale_price > 0
              AND c.category IS NOT NULL
              AND EXISTS (
                  SELECT 1 FROM @cat_map m
                  WHERE m.nsl_category = @category
                    AND LOWER(c.category) LIKE '%' + m.keyword + '%'
              )
            ORDER BY NEWID();
        END;

        DECLARE @pickedCount INT = (SELECT COUNT(*) FROM @picks);

        -- If the chosen category had too few matches, relabel as Mixed Goods
        -- and pick from the whole catalog so the pallet is still useful.
        IF @pickedCount < @items_per_pallet AND @category <> 'Mixed Goods'
        BEGIN
            SET @category = 'Mixed Goods';
            DELETE FROM @picks;
            INSERT INTO @picks
            SELECT TOP (@items_per_pallet)
                c.lpn, c.upc, c.asin, c.title, c.brand, c.category,
                c.msrp, c.unit_cost, c.wholesale_price, c.condition, c.product_image_url
            FROM dbo.lpn_catalog c
            WHERE c.title IS NOT NULL
              AND c.msrp > 0 AND c.unit_cost > 0 AND c.wholesale_price > 0
            ORDER BY NEWID();
        END;

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
        FROM @picks p;

        INSERT INTO @created (id, display_name, sold_at, item_count)
        VALUES (@id, @name, @soldAt, (SELECT COUNT(*) FROM @picks));
    END;

    SELECT id AS manifest_id, display_name, sold_at, item_count
    FROM @created
    ORDER BY sold_at DESC;
END;
GO

GRANT EXECUTE ON dbo.sp_GenerateGhostBackstock TO nsl_api;

PRINT 'Ghost backstock generator: category-aware version applied. Old ghost pallets cleared.';

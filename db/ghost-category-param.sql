-- ============================================================================
-- ghost-category-param.sql  (2026-06-18)
--
-- Rob's ask #5: let the ghost backstock generator be pinned to ONE category so
-- the generated pallets aren't a random mix (no "sweatshirts with microwaves").
--
-- Adds an optional @force_category param to sp_GenerateGhostBackstock:
--   * NULL/'' (default) → old behavior: each pallet gets a random category.
--   * set            → every pallet uses that category, and we DON'T fall back
--                       to Mixed Goods if matches run short (that fallback is
--                       what mixed unrelated goods in — the thing being fixed).
--                       A forced category just takes the matches it finds.
--
-- Idempotent. Apply against sqldb-nsl-prod after ghost-backstock-category-filter.sql.
-- ============================================================================
SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.sp_GenerateGhostBackstock', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_GenerateGhostBackstock;
GO
CREATE PROCEDURE dbo.sp_GenerateGhostBackstock
    @pallet_count     INT = 5,
    @items_per_pallet INT = 12,
    @force_category   NVARCHAR(50) = NULL    -- pin every pallet to this NSL category
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @cat_map TABLE (nsl_category NVARCHAR(50), keyword NVARCHAR(80));
    INSERT INTO @cat_map(nsl_category, keyword) VALUES
        ('Apparel','apparel'),('Apparel','women'),('Apparel','men'),('Apparel','girls'),
        ('Apparel','boys'),('Apparel','baby apparel'),('Apparel','swim'),('Apparel','outerwear'),
        ('Apparel','denim'),('Apparel','tops'),('Apparel','dresses'),('Apparel','intimate'),
        ('Apparel','hosiery'),('Apparel','shapewear'),('Apparel','athletic'),('Apparel','sleep'),
        ('Apparel','underwear'),('Apparel','sportswear'),('Apparel','accessories'),
        ('Apparel','contemporary'),('Apparel','maternity'),
        -- garment product-types (matches supplier categories like the Bella
        -- Canvas catalog: "T-Shirt", "Woven", "Fleece", "Polo").
        ('Apparel','t-shirt'),('Apparel','tee'),('Apparel','shirt'),('Apparel','polo'),
        ('Apparel','fleece'),('Apparel','woven'),('Apparel','knit'),('Apparel','sweat'),
        ('Apparel','hoodie'),('Apparel','tank'),('Apparel','jacket'),('Apparel','pant'),
        ('Apparel','short'),('Apparel','jersey'),
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

    DECLARE @categories TABLE (n NVARCHAR(50));
    INSERT INTO @categories(n) VALUES
        ('Apparel'),('Electronics'),('Appliances'),('Furniture'),
        ('Home Goods'),('Holiday'),('Mixed Goods');

    DECLARE @forced NVARCHAR(50) = NULLIF(LTRIM(RTRIM(@force_category)), '');

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
        -- forced category wins; otherwise random per pallet.
        DECLARE @category NVARCHAR(50) =
            COALESCE(@forced, (SELECT TOP 1 n FROM @categories ORDER BY NEWID()));
        DECLARE @mode VARCHAR(20) = (SELECT TOP 1 m FROM @modes ORDER BY NEWID());

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

        -- Relabel-to-Mixed fallback ONLY when the category was randomly chosen.
        -- A forced category keeps its own (possibly fewer) items so we never
        -- contaminate a pinned category with unrelated goods.
        IF @pickedCount < @items_per_pallet AND @category <> 'Mixed Goods' AND @forced IS NULL
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

PRINT 'sp_GenerateGhostBackstock: optional @force_category added (pin one category; no Mixed-Goods fallback when forced).';

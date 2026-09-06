-- ----------------------------------------------------------------------------
-- upc-lookup-cache.sql  (2026-09-06)
--
-- The scanner's online UPC fallback (UPCitemdb free tier) is capped at 100
-- calls/day and /api/lookup called it on EVERY scan, catalog hit or not. This
-- adds:
--   * dbo.upc_lookup_cache — one row per UPC we have ever asked the provider
--     about (hit OR miss), so the same barcode never costs a second call.
--   * dbo.upc_lookup_log   — one row per provider call, so we can count today's
--     usage against the cap and surface it in the scanner.
--   * procs for the API (nsl_api only has EXECUTE on procs, not table rights).
--
-- Apply by hand against sqldb-nsl-prod (see nsl-stack-and-ops memory).
-- Idempotent: safe to re-run.
-- ----------------------------------------------------------------------------

IF OBJECT_ID('dbo.upc_lookup_cache', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.upc_lookup_cache (
        upc            VARCHAR(20)      NOT NULL PRIMARY KEY,
        status         VARCHAR(16)      NOT NULL,   -- hit | miss | rate_limited | error
        source         VARCHAR(40)      NOT NULL DEFAULT 'upcitemdb',
        title          NVARCHAR(500)    NULL,
        brand          NVARCHAR(200)    NULL,
        description    NVARCHAR(MAX)    NULL,
        category       NVARCHAR(200)    NULL,
        market_price   DECIMAL(12, 2)   NULL,
        asin           VARCHAR(20)      NULL,
        ean            VARCHAR(20)      NULL,
        image_url      NVARCHAR(1000)   NULL,
        looked_up_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        provider_calls INT              NOT NULL DEFAULT 1,
        cache_hits     INT              NOT NULL DEFAULT 0,
        last_hit_at    DATETIME2        NULL
    );
END;
GO

IF OBJECT_ID('dbo.upc_lookup_log', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.upc_lookup_log (
        id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        upc         VARCHAR(20)          NOT NULL,
        outcome     VARCHAR(16)          NOT NULL,   -- hit | miss | rate_limited | error
        http_status INT                  NULL,
        called_at   DATETIME2            NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_upc_lookup_log_called_at ON dbo.upc_lookup_log (called_at);
END;
GO

-- ----------------------------------------------------------------------------
-- sp_UpcCacheGet: return the cached row (if any) and bump the served counter.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.sp_UpcCacheGet
    @upc VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.upc_lookup_cache
       SET cache_hits = cache_hits + 1, last_hit_at = SYSUTCDATETIME()
     WHERE upc = @upc;

    SELECT upc, status, source, title, brand, description, category,
           market_price, asin, ean, image_url, looked_up_at, provider_calls, cache_hits
      FROM dbo.upc_lookup_cache
     WHERE upc = @upc;
END;
GO

-- ----------------------------------------------------------------------------
-- sp_UpcCachePut: upsert after a provider call. Also writes the log row so
-- the daily counter is exact.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.sp_UpcCachePut
    @upc          VARCHAR(20),
    @status       VARCHAR(16),
    @http_status  INT             = NULL,
    @source       VARCHAR(40)     = 'upcitemdb',
    @title        NVARCHAR(500)   = NULL,
    @brand        NVARCHAR(200)   = NULL,
    @description  NVARCHAR(MAX)   = NULL,
    @category     NVARCHAR(200)   = NULL,
    @market_price DECIMAL(12, 2)  = NULL,
    @asin         VARCHAR(20)     = NULL,
    @ean          VARCHAR(20)     = NULL,
    @image_url    NVARCHAR(1000)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.upc_lookup_log (upc, outcome, http_status) VALUES (@upc, @status, @http_status);

    MERGE dbo.upc_lookup_cache AS t
    USING (SELECT @upc AS upc) AS s ON t.upc = s.upc
    WHEN MATCHED THEN UPDATE SET
        status         = @status,
        source         = @source,
        -- keep good data if a later call degrades (e.g. hit then rate_limited)
        title          = COALESCE(@title, t.title),
        brand          = COALESCE(@brand, t.brand),
        description    = COALESCE(@description, t.description),
        category       = COALESCE(@category, t.category),
        market_price   = COALESCE(@market_price, t.market_price),
        asin           = COALESCE(@asin, t.asin),
        ean            = COALESCE(@ean, t.ean),
        image_url      = COALESCE(@image_url, t.image_url),
        looked_up_at   = SYSUTCDATETIME(),
        provider_calls = t.provider_calls + 1
    WHEN NOT MATCHED THEN INSERT
        (upc, status, source, title, brand, description, category, market_price, asin, ean, image_url)
        VALUES
        (@upc, @status, @source, @title, @brand, @description, @category, @market_price, @asin, @ean, @image_url);
END;
GO

-- ----------------------------------------------------------------------------
-- sp_UpcLookupStats: today's provider usage (UTC day, which is how the free
-- tier counts) plus cache size. Feeds the badge on the scan page.
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE dbo.sp_UpcLookupStats
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @day_start DATETIME2 = CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2);

    SELECT
        (SELECT COUNT(*) FROM dbo.upc_lookup_log WHERE called_at >= @day_start)                              AS calls_today,
        (SELECT COUNT(*) FROM dbo.upc_lookup_log WHERE called_at >= @day_start AND outcome = 'hit')          AS hits_today,
        (SELECT COUNT(*) FROM dbo.upc_lookup_log WHERE called_at >= @day_start AND outcome = 'rate_limited') AS rate_limited_today,
        (SELECT MAX(called_at) FROM dbo.upc_lookup_log WHERE outcome = 'rate_limited' AND called_at >= @day_start) AS last_rate_limited_at,
        (SELECT COUNT(*) FROM dbo.upc_lookup_cache)                                                          AS cache_rows,
        (SELECT COUNT(*) FROM dbo.upc_lookup_cache WHERE status = 'hit')                                     AS cache_hit_rows,
        (SELECT ISNULL(SUM(cache_hits), 0) FROM dbo.upc_lookup_cache)                                        AS cache_serves_total,
        (SELECT COUNT(*) FROM dbo.upc_lookup_cache WHERE last_hit_at >= @day_start)                          AS cache_serves_today;
END;
GO

GRANT EXECUTE ON dbo.sp_UpcCacheGet     TO nsl_api;
GRANT EXECUTE ON dbo.sp_UpcCachePut     TO nsl_api;
GRANT EXECUTE ON dbo.sp_UpcLookupStats  TO nsl_api;
GO

PRINT 'upc-lookup-cache: tables, procs, grants in place.';

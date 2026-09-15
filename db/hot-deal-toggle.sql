-- ============================================================================
-- Hot Deals manual toggle (Rob, 2026-09-14): "I think having a toggle switch
-- for hot deals would be good. This way we can keep it fresh every couple of
-- days." Adds is_hot_deal / hot_deal_at to dbo.manifests so staff can feature
-- a box on the Hot Deals page without it needing a sale price.
--
-- Deliberately does NOT re-create dbo.v_pallets or dbo.v_public_pallets —
-- those views are load-bearing and rebuilding them is unnecessary risk. The
-- API reaches the two new columns by joining dbo.manifests directly wherever
-- it needs them (PalletsFunction.cs: ListPallets/GetPallet/UpdatePallet join
-- v_pallets to manifests on manifest_id = id; PublicPallets joins
-- v_public_pallets to manifests the same way).
--
-- ORDER MATTERS: RUN THIS SQL FIRST, THEN DEPLOY THE CODE. Never the other
-- way round, and never "we'll run the SQL later" — merging to the default
-- branch IS the deploy here, so later means after the fact.
--
-- Why this direction is not optional. The deployed code names is_hot_deal and
-- hot_deal_at in GET /api/public/pallets (PalletsFunction.PublicPallets), the
-- anonymous feed that index.html, shop.html and faq.html all load through
-- js/site.js. Against a database without these columns that SELECT throws, and
-- what breaks is not the new toggle quietly doing nothing — it is every
-- inventory grid, the recently-sold strip, the live counts bar and the cart
-- drawer's box check, on every page, for every visitor, until somebody runs
-- this file by hand. The staff admin list, detail and PATCH paths go with it.
--
-- The other direction is genuinely safe, which is the whole reason SQL goes
-- first: is_hot_deal is BIT NOT NULL with a DEFAULT 0 and hot_deal_at is a
-- nullable DATETIME2, so a database that has these columns while the old code
-- is still running behaves exactly as before. There is no window to lose.
--
-- Idempotent, so re-running it costs nothing. Both columns are ALREADY APPLIED
-- to production; this ordering note is for the next environment that needs
-- them — staging, a restored copy, a fresh build.
-- ============================================================================
SET NOCOUNT ON;

IF COL_LENGTH('dbo.manifests', 'is_hot_deal') IS NULL
    ALTER TABLE dbo.manifests ADD is_hot_deal BIT NOT NULL CONSTRAINT DF_manifests_is_hot_deal DEFAULT 0;
GO

IF COL_LENGTH('dbo.manifests', 'hot_deal_at') IS NULL
    ALTER TABLE dbo.manifests ADD hot_deal_at DATETIME2 NULL;
GO

PRINT 'hot-deal-toggle: dbo.manifests.is_hot_deal + hot_deal_at added; v_pallets/v_public_pallets left untouched, API joins manifests directly for these columns.';

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
-- Idempotent. Safe to apply any time, independent of the code deploy — these
-- are a nullable column and a defaulted column, so adding them changes
-- nothing for the currently running code.
-- ============================================================================
SET NOCOUNT ON;

IF COL_LENGTH('dbo.manifests', 'is_hot_deal') IS NULL
    ALTER TABLE dbo.manifests ADD is_hot_deal BIT NOT NULL CONSTRAINT DF_manifests_is_hot_deal DEFAULT 0;
GO

IF COL_LENGTH('dbo.manifests', 'hot_deal_at') IS NULL
    ALTER TABLE dbo.manifests ADD hot_deal_at DATETIME2 NULL;
GO

PRINT 'hot-deal-toggle: dbo.manifests.is_hot_deal + hot_deal_at added; v_pallets/v_public_pallets left untouched, API joins manifests directly for these columns.';

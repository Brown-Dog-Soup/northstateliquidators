-- ============================================================================
-- New boxes default to "Mega Box" (2026-09-14, Rob request)
-- Adds DEFAULT constraint on dbo.manifests.box_size so any INSERT that
-- omits box_size receives 'mega_box' as the default value.
-- This applies to new boxes created by sp_CreateManifest, sp_CreatePalletFromCatalog,
-- sp_GenerateGhostBackstock, and other procedures.
--
-- NOTE: sp_SoldToInventory includes box_size explicitly in its clone INSERT,
-- so cloned boxes retain the original box's size — they do NOT become Mega Box.
--
-- Idempotent — apply is safe any time, independent of code deploys.
-- A default constraint only affects new INSERTs; existing rows are unaffected.
-- ============================================================================
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_manifests_box_size')
    ALTER TABLE dbo.manifests
        ADD CONSTRAINT DF_manifests_box_size DEFAULT 'mega_box' FOR box_size;
GO

-- Report: How many existing boxes still have no size assigned?
-- The result of this query tells us whether backfilling is needed.
SELECT
    COUNT(*) AS boxes_without_size_existing
FROM dbo.manifests
WHERE box_size IS NULL;
GO

PRINT 'box-size-default: DEFAULT constraint DF_manifests_box_size added; new boxes now default to mega_box.';

-- ============================================================================
-- NSL Admin Portal — schema additions + helper procs.
-- Apply against sqldb-nsl-prod after the base schema and power-apps-procs.sql.
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- manifests — add admin-portal columns
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.manifests', 'display_name') IS NULL
    ALTER TABLE dbo.manifests ADD display_name NVARCHAR(200) NULL;
IF COL_LENGTH('dbo.manifests', 'photo_url') IS NULL
    ALTER TABLE dbo.manifests ADD photo_url NVARCHAR(2000) NULL;
IF COL_LENGTH('dbo.manifests', 'pallet_number') IS NULL
    ALTER TABLE dbo.manifests ADD pallet_number INT NULL;
GO

-- ----------------------------------------------------------------------------
-- seq_pallet_number — auto-increments for pallet auto-naming.
-- Sequence (rather than IDENTITY) so we can backfill existing rows safely.
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'seq_pallet_number')
    EXEC('CREATE SEQUENCE dbo.seq_pallet_number AS INT START WITH 1 INCREMENT BY 1;');
GO

-- Backfill any existing manifests that don't have a pallet_number yet.
UPDATE dbo.manifests
SET pallet_number = NEXT VALUE FOR dbo.seq_pallet_number
WHERE pallet_number IS NULL;
GO

-- ----------------------------------------------------------------------------
-- sp_CreateManifest
-- Used by the admin portal "New Pallet" button. Auto-assigns pallet_number
-- and a default display_name like "Pallet #042" if none provided.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_CreateManifest', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_CreateManifest;
GO
CREATE PROCEDURE dbo.sp_CreateManifest
    @display_name      NVARCHAR(200) = NULL,
    @source            NVARCHAR(200) = NULL,
    @pallet_reference  NVARCHAR(200) = NULL,
    @notes             NVARCHAR(MAX) = NULL,
    @total_cost        DECIMAL(12,2) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @id UNIQUEIDENTIFIER = NEWID();
    DECLARE @num INT = NEXT VALUE FOR dbo.seq_pallet_number;
    DECLARE @name NVARCHAR(200) =
        COALESCE(NULLIF(LTRIM(RTRIM(@display_name)), ''),
                 'Pallet #' + RIGHT('000' + CAST(@num AS VARCHAR(10)), 3));

    INSERT INTO dbo.manifests
        (id, source, pallet_reference, status, sell_mode,
         display_name, pallet_number, notes, total_cost)
    VALUES
        (@id, @source, @pallet_reference, 'receiving', 'undecided',
         @name, @num, @notes, @total_cost);

    SELECT @id AS id, @num AS pallet_number, @name AS display_name;
END;
GO

-- ----------------------------------------------------------------------------
-- sp_SetSellMode
-- Updates a manifest's sell_mode. Used by the "Sell as Lot / Individualize /
-- Mixed" buttons on the admin pallet-detail screen.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.sp_SetSellMode', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_SetSellMode;
GO
CREATE PROCEDURE dbo.sp_SetSellMode
    @manifest_id UNIQUEIDENTIFIER,
    @sell_mode   VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF @sell_mode NOT IN ('undecided','lot','individual','mixed')
    BEGIN
        RAISERROR('sell_mode must be one of undecided | lot | individual | mixed.', 16, 1);
        RETURN;
    END;

    UPDATE dbo.manifests
    SET sell_mode  = @sell_mode,
        status     = CASE @sell_mode
                        WHEN 'lot'         THEN 'lotted'
                        WHEN 'individual'  THEN 'individualized'
                        WHEN 'mixed'       THEN 'individualized'
                        ELSE status
                     END,
        updated_at = SYSUTCDATETIME()
    WHERE id = @manifest_id;

    SELECT id, sell_mode, status FROM dbo.manifests WHERE id = @manifest_id;
END;
GO

-- ----------------------------------------------------------------------------
-- v_pallets — convenience view powering the admin "Pallets" gallery.
-- One row per manifest with rolled-up stats from line_items.
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
    m.status,
    m.sell_mode,
    m.total_cost,
    m.photo_url,
    COUNT(li.id)            AS item_count,
    SUM(li.qty)             AS unit_count,
    SUM(li.est_msrp * li.qty)   AS total_msrp,
    SUM(li.est_resale * li.qty) AS total_est_resale,
    SUM(CASE WHEN li.enrich_status = 'hit' THEN 1 ELSE 0 END) AS items_enriched
FROM dbo.manifests m
LEFT JOIN dbo.line_items li ON li.manifest_id = m.id
GROUP BY m.id, m.pallet_number, m.display_name, m.source, m.pallet_reference,
         m.received_date, m.status, m.sell_mode, m.total_cost, m.photo_url;
GO

-- ----------------------------------------------------------------------------
-- Grants for nsl_api so Power Apps can call/read everything
-- ----------------------------------------------------------------------------
GRANT EXECUTE ON dbo.sp_CreateManifest TO nsl_api;
GRANT EXECUTE ON dbo.sp_SetSellMode    TO nsl_api;
GRANT SELECT ON dbo.v_pallets          TO nsl_api;
GRANT UPDATE ON dbo.manifests          TO nsl_api;
GRANT UPDATE ON dbo.line_items         TO nsl_api;

PRINT 'Admin portal SQL additions applied.';

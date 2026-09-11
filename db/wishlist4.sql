-- ============================================================================
-- Wishlist 4 (Rob's 2026-09-11 list) — DB layer. See docs/WISHLIST4-BUILD.md.
--
--   B1     admin-marked SOLD boxes show in Sales      (API only — no schema)
--   B2/B6  "Sold → inventory": sp_SoldToInventory clones the box into Draft
--          and stamps sold_to_inventory_at on the original; v_public_pallets
--          keeps the fake-sold original visible for 48 h, then hides it.
--   B3     manifests.weight_lbs
--   B5     v_pallets.units_with_cost (units, not rows)
--   B7     dbo.manifest_history (audit trail; rows written by the API)
--   B8     line_items.is_highlight → v_pallets highlight_title/msrp/photo
--   B9     manifests.live_at (stamped by sp_SetPublishState) for Just Dropped
--   F3     dbo.members + sp_RegisterMember (member number YY + 5 digits)
--   —      manifests.box_size (mega_box | mini_pallet | full_pallet | individual)
--          drives the Shop pages on the website
--
-- Idempotent — re-running is safe. Apply against sqldb-nsl-prod AFTER
-- db/square-invoices.sql (the v_pallets definition being extended here).
-- Order inside this file: columns → tables → procs → views → grants.
-- ============================================================================

SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- 1. Columns
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.manifests', 'box_size') IS NULL
    ALTER TABLE dbo.manifests ADD box_size VARCHAR(20) NULL;
IF COL_LENGTH('dbo.manifests', 'weight_lbs') IS NULL
    ALTER TABLE dbo.manifests ADD weight_lbs DECIMAL(8,2) NULL;
IF COL_LENGTH('dbo.manifests', 'live_at') IS NULL
    ALTER TABLE dbo.manifests ADD live_at DATETIME2 NULL;
IF COL_LENGTH('dbo.manifests', 'sold_to_inventory_at') IS NULL
    ALTER TABLE dbo.manifests ADD sold_to_inventory_at DATETIME2 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_manifests_box_size')
    ALTER TABLE dbo.manifests
        ADD CONSTRAINT CK_manifests_box_size
        CHECK (box_size IN ('mega_box','mini_pallet','full_pallet','individual'));
GO

-- B9 backfill, once: boxes that are already live count as "went live" at their
-- last update. updated_at is bumped by ANY edit (price, notes, photo…), so a
-- long-live box that staff touched in the last 48 h would otherwise land in
-- "Just Dropped" on launch day — those get pushed just outside the window.
-- Only rows still NULL are touched, so re-runs are no-ops.
UPDATE dbo.manifests
SET    live_at = COALESCE(live_at,
                          CASE WHEN updated_at >= DATEADD(HOUR, -48, SYSUTCDATETIME())
                               THEN DATEADD(HOUR, -49, SYSUTCDATETIME())
                               ELSE updated_at END)
WHERE  publish_state = 'live' AND live_at IS NULL;
GO

IF COL_LENGTH('dbo.line_items', 'is_highlight') IS NULL
    ALTER TABLE dbo.line_items
        ADD is_highlight BIT NOT NULL
            CONSTRAINT DF_line_items_is_highlight DEFAULT 0;
GO

-- ----------------------------------------------------------------------------
-- 2. Tables
-- ----------------------------------------------------------------------------

-- B7 manifest_history — one row per changed field (publish_state | list_price |
-- sale_price | box_size | sell_mode | sold_to_inventory). Written by the API,
-- not by triggers. changed_by = SWA userDetails (email) | 'square' | NULL.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'manifest_history' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.manifest_history (
        id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        manifest_id UNIQUEIDENTIFIER     NOT NULL,
        changed_at  DATETIME2            NOT NULL CONSTRAINT DF_manifest_history_changed_at DEFAULT SYSUTCDATETIME(),
        changed_by  NVARCHAR(200)        NULL,
        field       VARCHAR(40)          NOT NULL,
        old_value   NVARCHAR(400)        NULL,
        new_value   NVARCHAR(400)        NULL,
        CONSTRAINT FK_manifest_history_manifest FOREIGN KEY (manifest_id) REFERENCES dbo.manifests(id)
    );
    CREATE INDEX IX_manifest_history_manifest ON dbo.manifest_history (manifest_id, changed_at DESC);
END;
GO

-- F3 members — lightweight signup capture. This row IS the future
-- dbo.resellers row (RESELLER-PROGRAM-DESIGN.md): external_id / business_name /
-- verified_at columns get added later; member_number is the public key.
-- email is stored lower-cased + trimmed; the DB collation (CI) makes the
-- UNIQUE constraint case-insensitive as well.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'members' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.members (
        id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        member_number CHAR(7)       NOT NULL CONSTRAINT UQ_members_member_number UNIQUE,   -- '2600001'
        first_name    NVARCHAR(100) NOT NULL,
        last_name     NVARCHAR(100) NOT NULL,
        email         NVARCHAR(320) NOT NULL CONSTRAINT UQ_members_email UNIQUE,
        phone         VARCHAR(30)   NULL,
        city          NVARCHAR(120) NULL,
        state         VARCHAR(2)    NULL,        -- upper-cased 2-letter
        zip           VARCHAR(10)   NULL,
        how_heard     NVARCHAR(200) NULL,
        source        NVARCHAR(40)  NOT NULL CONSTRAINT DF_members_source DEFAULT 'web',    -- web | floor | import
        created_at    DATETIME2     NOT NULL CONSTRAINT DF_members_created_at DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 3. Procs
-- ----------------------------------------------------------------------------

-- B9/B2 sp_SetPublishState — same signature, result set and item-level effects
-- as db/wishlist2-part2.sql. Adds:
--   live_at              = the moment the box most recently BECAME live
--                          (re-clicking Live on a live box does not refresh it;
--                          draft → live again does)
--   sold_to_inventory_at = cleared on live/draft (staff "undoing" a fake sale)
IF OBJECT_ID('dbo.sp_SetPublishState', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_SetPublishState;
GO
CREATE PROCEDURE dbo.sp_SetPublishState
    @manifest_id   UNIQUEIDENTIFIER,
    @publish_state VARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;

    IF @publish_state NOT IN ('draft','live','ghost','sold')
    BEGIN
        RAISERROR('publish_state must be one of draft | live | ghost | sold.', 16, 1);
        RETURN;
    END;

    BEGIN TRAN;

    UPDATE dbo.manifests
    SET publish_state = @publish_state,
        is_ghost = CASE WHEN @publish_state = 'ghost' THEN 1 ELSE 0 END,
        sold_at  = CASE
                       WHEN @publish_state IN ('ghost','sold') THEN COALESCE(sold_at, SYSUTCDATETIME())
                       ELSE NULL          -- live / draft clear the sold timestamp
                   END,
        live_at = CASE WHEN @publish_state = 'live' AND (publish_state <> 'live' OR live_at IS NULL)
                       THEN SYSUTCDATETIME() ELSE live_at END,
        sold_to_inventory_at = CASE WHEN @publish_state IN ('live','draft') THEN NULL ELSE sold_to_inventory_at END,
        updated_at = SYSUTCDATETIME()
    WHERE id = @manifest_id;

    -- Item-level inventory effect:
    --   sold        → items leave inventory (stamp sold_at)
    --   live/draft  → items are back in play (clear sold_at)
    --   ghost       → leave items as-is (ghost is fake; is_ghost wins in v_inventory)
    IF @publish_state = 'sold'
        UPDATE dbo.line_items
        SET sold_at = COALESCE(sold_at, SYSUTCDATETIME())
        WHERE manifest_id = @manifest_id;
    ELSE IF @publish_state IN ('live','draft')
        UPDATE dbo.line_items
        SET sold_at = NULL
        WHERE manifest_id = @manifest_id;

    COMMIT TRAN;

    SELECT id, publish_state, is_ghost, sold_at, status
    FROM dbo.manifests WHERE id = @manifest_id;
END;
GO

-- B2/B6 sp_SoldToInventory — ONE button. The original box goes SOLD with the
-- fake-sale marker (public for 48 h via v_public_pallets, then gone); a clone
-- with a new BOX # and the same items appears in Draft immediately.
-- The clone deliberately does NOT copy checkout_link_id / checkout_order_id /
-- checkout_url / checkout_created_at / invoice_id / invoice_url / live_at /
-- sold_at / sold_to_inventory_at / archived_at. The original keeps its links;
-- the API retires an open Square link afterwards.
IF OBJECT_ID('dbo.sp_SoldToInventory', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_SoldToInventory;
GO
CREATE PROCEDURE dbo.sp_SoldToInventory
    @manifest_id UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @ps VARCHAR(20), @ghost BIT, @archived DATETIME2, @orig_num INT, @orig_name NVARCHAR(200);
    SELECT @ps = publish_state, @ghost = is_ghost, @archived = archived_at,
           @orig_num = pallet_number, @orig_name = display_name
    FROM dbo.manifests WHERE id = @manifest_id;

    IF @ps IS NULL            BEGIN RAISERROR('Box not found.', 16, 1); RETURN; END;
    IF @ghost = 1 OR @ps = 'ghost' BEGIN RAISERROR('Ghost boxes are already fictitious — nothing to clone.', 16, 1); RETURN; END;
    IF @ps = 'sold'           BEGIN RAISERROR('Box is already sold.', 16, 1); RETURN; END;
    IF @archived IS NOT NULL  BEGIN RAISERROR('Restore the box before using Sold to inventory.', 16, 1); RETURN; END;

    DECLARE @now DATETIME2 = SYSUTCDATETIME();
    DECLARE @clone_id UNIQUEIDENTIFIER = NEWID();
    DECLARE @clone_num INT = NEXT VALUE FOR dbo.seq_pallet_number;

    BEGIN TRAN;

    -- (a) clone: same name (NOT "(copy)" — it is the same physical box), draft,
    --     fresh timestamps, no checkout/invoice/sold/live markers.
    INSERT INTO dbo.manifests
        (id, source, pallet_reference, received_date, status, sell_mode, publish_state,
         display_name, pallet_number, category, total_cost, photo_url, notes, public_description,
         list_price, sale_price, box_size, weight_lbs, is_ghost)
    SELECT
        @clone_id, source, pallet_reference, @now, status, sell_mode, 'draft',
        display_name, @clone_num, category, total_cost, photo_url,
        CONCAT(COALESCE(notes + CHAR(10), ''), 'Cloned from BOX #', @orig_num, ' via Sold -> inventory on ', CONVERT(VARCHAR(19), @now, 120), ' UTC.'),
        public_description, list_price, sale_price, box_size, weight_lbs, 0
    FROM dbo.manifests WHERE id = @manifest_id;

    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition, photo_blob_url, enrich_status, enrich_source,
         title, description, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
         is_highlight, created_at, enriched_at)
    SELECT
        NEWID(), @clone_id, upc, lpn, asin, qty, condition, photo_blob_url, enrich_status, enrich_source,
        title, description, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
        is_highlight, @now, enriched_at
    FROM dbo.line_items WHERE manifest_id = @manifest_id;
    DECLARE @copied INT = @@ROWCOUNT;

    -- (b) original: SOLD + fake-sale marker; items consumed (inventory lives on in the clone).
    UPDATE dbo.manifests
    SET publish_state = 'sold', is_ghost = 0, sold_at = @now, sold_to_inventory_at = @now, updated_at = @now
    WHERE id = @manifest_id;
    UPDATE dbo.line_items SET sold_at = COALESCE(sold_at, @now) WHERE manifest_id = @manifest_id;

    COMMIT TRAN;

    SELECT @manifest_id AS original_id, @orig_num AS original_pallet_number,
           @clone_id AS clone_id, @clone_num AS clone_pallet_number,
           @orig_name AS clone_display_name, @copied AS items_copied;
END;
GO

-- F3 sp_RegisterMember — idempotent on email (returns the existing number with
-- already_registered = 1). Member number = Eastern-local YY + 5-digit per-year
-- counter ('2600001'); an app lock serializes two signups in the same second.
IF OBJECT_ID('dbo.sp_RegisterMember', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RegisterMember;
GO
CREATE PROCEDURE dbo.sp_RegisterMember
    @first_name NVARCHAR(100),
    @last_name  NVARCHAR(100),
    @email      NVARCHAR(320),
    @phone      VARCHAR(30)   = NULL,
    @city       NVARCHAR(120) = NULL,
    @state      VARCHAR(2)    = NULL,
    @zip        VARCHAR(10)   = NULL,
    @how_heard  NVARCHAR(200) = NULL,
    @source     NVARCHAR(40)  = 'web'
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    SET @email = LOWER(LTRIM(RTRIM(@email)));
    SET @state = UPPER(NULLIF(LTRIM(RTRIM(@state)), ''));

    BEGIN TRAN;
    -- serialize number generation: two signups in the same second must not collide.
    -- sp_getapplock does NOT raise on timeout — it returns <0 and execution
    -- continues — so check the return code or a slow burst runs unserialized
    -- and the second INSERT dies on UQ_members_member_number instead.
    DECLARE @lock INT;
    EXEC @lock = sp_getapplock @Resource = 'nsl_member_number', @LockMode = 'Exclusive',
                               @LockOwner = 'Transaction', @LockTimeout = 5000;
    IF @lock < 0
    BEGIN
        ROLLBACK TRAN;
        RAISERROR('Could not reserve a member number right now — please try again.', 16, 1);
        RETURN;
    END;

    DECLARE @existing CHAR(7) = (SELECT member_number FROM dbo.members WHERE email = @email);
    IF @existing IS NOT NULL
    BEGIN
        COMMIT TRAN;
        SELECT @existing AS member_number, CAST(1 AS BIT) AS already_registered;
        RETURN;
    END;

    -- year = Eastern local year ("first person in 2026 = 2600001"), 5-digit per-year counter
    DECLARE @yy CHAR(2) = RIGHT(CAST(YEAR(SYSDATETIMEOFFSET() AT TIME ZONE 'Eastern Standard Time') AS VARCHAR(4)), 2);
    DECLARE @next INT = ISNULL((SELECT MAX(CAST(RIGHT(member_number, 5) AS INT))
                                FROM dbo.members WHERE LEFT(member_number, 2) = @yy), 0) + 1;
    DECLARE @num CHAR(7) = @yy + RIGHT('00000' + CAST(@next AS VARCHAR(5)), 5);

    INSERT INTO dbo.members (member_number, first_name, last_name, email, phone, city, state, zip, how_heard, source)
    VALUES (@num, LTRIM(RTRIM(@first_name)), LTRIM(RTRIM(@last_name)), @email,
            NULLIF(LTRIM(RTRIM(@phone)), ''), NULLIF(LTRIM(RTRIM(@city)), ''), @state,
            NULLIF(LTRIM(RTRIM(@zip)), ''), NULLIF(LTRIM(RTRIM(@how_heard)), ''), COALESCE(@source, 'web'));
    COMMIT TRAN;

    SELECT @num AS member_number, CAST(0 AS BIT) AS already_registered;
END;
GO

-- ----------------------------------------------------------------------------
-- 4. Views
-- ----------------------------------------------------------------------------

-- v_pallets — FULL re-declare: db/square-invoices.sql definition + box_size,
-- weight_lbs, live_at, sold_to_inventory_at, units_with_cost (B5),
-- condition_mix, highlight_title/msrp/photo (B8). items_with_cost is KEPT for
-- compatibility.
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
    m.publish_state,
    m.list_price,
    m.sale_price,
    m.category,
    m.archived_at,
    m.is_ghost,
    m.total_cost,
    m.photo_url,
    m.notes,
    m.public_description,
    m.invoice_id,
    m.invoice_url,
    m.box_size,                 -- NEW
    m.weight_lbs,               -- NEW (B3)
    m.live_at,                  -- NEW (B9)
    m.sold_to_inventory_at,     -- NEW (B2; staff only — never selected by public routes)
    agg.item_count,
    agg.unit_count,
    agg.total_msrp,
    agg.total_cost_units,
    agg.total_wholesale,
    agg.total_est_resale,
    agg.items_enriched,
    agg.items_with_cost,
    agg.units_with_cost,        -- NEW (B5)
    cond.condition_mix,         -- NEW  e.g. 'new:12,customer_return:4,untested:1'
    hl.highlight_title,         -- NEW (B8)
    hl.highlight_msrp,          -- NEW
    hl.highlight_photo          -- NEW (raw blob URL; API signs it)
FROM dbo.manifests m
OUTER APPLY (
    SELECT
        COUNT(li.id)                      AS item_count,
        SUM(li.qty)                       AS unit_count,
        SUM(li.est_msrp * li.qty)         AS total_msrp,
        SUM(li.unit_cost * li.qty)        AS total_cost_units,
        SUM(li.wholesale_price * li.qty)  AS total_wholesale,
        SUM(li.est_resale * li.qty)       AS total_est_resale,
        SUM(CASE WHEN li.enrich_status = 'hit'  THEN 1      ELSE 0 END) AS items_enriched,
        SUM(CASE WHEN li.unit_cost IS NOT NULL  THEN 1      ELSE 0 END) AS items_with_cost,
        SUM(CASE WHEN li.unit_cost IS NOT NULL  THEN li.qty ELSE 0 END) AS units_with_cost
    FROM dbo.line_items li
    WHERE li.manifest_id = m.id
) agg
OUTER APPLY (
    SELECT STRING_AGG(CAST(x.cond AS NVARCHAR(60)) + ':' + CAST(x.units AS VARCHAR(10)), ',')
               WITHIN GROUP (ORDER BY x.units DESC) AS condition_mix
    FROM (
        SELECT COALESCE(li.condition, 'untested') AS cond, SUM(li.qty) AS units
        FROM dbo.line_items li WHERE li.manifest_id = m.id
        GROUP BY COALESCE(li.condition, 'untested')
    ) x
) cond
OUTER APPLY (
    SELECT TOP 1 li.title AS highlight_title, li.est_msrp AS highlight_msrp, li.photo_blob_url AS highlight_photo
    FROM dbo.line_items li
    WHERE li.manifest_id = m.id AND li.is_highlight = 1
    ORDER BY li.est_msrp DESC, li.created_at ASC
) hl;
GO

-- v_public_pallets — FULL re-declare: db/wishlist3-part2.sql definition +
-- live_at, box_size, weight_lbs, condition_mix, highlight_* and the B2 48 h
-- fake-sold exclusion. sold_to_inventory_at is deliberately NOT a column here.
-- is_just_dropped is NOT a view column — the API computes it so the window
-- lives in one C# constant (PalletsFunction.JustDroppedHours).
IF OBJECT_ID('dbo.v_public_pallets', 'V') IS NOT NULL DROP VIEW dbo.v_public_pallets;
GO
CREATE VIEW dbo.v_public_pallets AS
SELECT
    p.manifest_id,
    p.pallet_number,
    p.display_name,
    p.category,
    p.publish_state,
    p.received_date,
    p.sold_at,
    p.live_at,                  -- NEW
    p.photo_url,
    p.public_description,
    p.box_size,                 -- NEW
    p.weight_lbs,               -- NEW
    p.item_count,
    p.unit_count,
    p.total_msrp,
    p.list_price,
    p.sale_price,
    p.condition_mix,            -- NEW
    p.highlight_title,          -- NEW
    p.highlight_msrp,           -- NEW
    p.highlight_photo,          -- NEW
    CAST(CASE WHEN p.publish_state IN ('ghost','sold') THEN 1 ELSE 0 END AS BIT) AS is_sold,
    CAST(CASE WHEN p.sale_price IS NOT NULL AND p.list_price IS NOT NULL
                   AND p.sale_price < p.list_price
              THEN 1 ELSE 0 END AS BIT) AS is_on_sale,
    COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p
WHERE p.archived_at IS NULL
  AND p.publish_state IN ('live','ghost','sold')
  -- B2: a fake-sold original is public for 48 h, then disappears (no scheduler)
  AND (p.sold_to_inventory_at IS NULL
       OR p.sold_to_inventory_at >= DATEADD(HOUR, -48, SYSUTCDATETIME()));
GO

-- ----------------------------------------------------------------------------
-- 5. Grants
-- ----------------------------------------------------------------------------
GRANT SELECT, INSERT, DELETE ON dbo.manifest_history TO nsl_api;
GRANT SELECT, INSERT         ON dbo.members          TO nsl_api;
GRANT EXECUTE ON dbo.sp_SetPublishState  TO nsl_api;
GRANT EXECUTE ON dbo.sp_SoldToInventory  TO nsl_api;
GRANT EXECUTE ON dbo.sp_RegisterMember   TO nsl_api;
GRANT SELECT  ON dbo.v_pallets           TO nsl_api;
GRANT SELECT  ON dbo.v_public_pallets    TO nsl_api;
GO

PRINT 'wishlist4: box_size/weight_lbs/live_at/sold_to_inventory_at + is_highlight; manifest_history + members; sp_SetPublishState/sp_SoldToInventory/sp_RegisterMember; v_pallets + v_public_pallets rebuilt.';

-- ============================================================================
-- Verification (run against a scratch box, then delete it):
--   EXEC dbo.sp_SetPublishState @id,'live'  → live_at set; again → unchanged;
--       'draft' then 'live' → refreshed.
--   EXEC dbo.sp_SoldToInventory @id         → original publish_state='sold',
--       sold_to_inventory_at set, items sold_at set; clone draft with same item
--       count, live_at IS NULL, new pallet_number; original visible in
--       v_public_pallets; UPDATE ... sold_to_inventory_at = DATEADD(HOUR,-49,
--       SYSUTCDATETIME()) → gone from v_public_pallets.
--   EXEC dbo.sp_RegisterMember 'A','B','X@Y.com' twice → same member_number,
--       second call already_registered=1; different email → next number.
--   SELECT units_with_cost, unit_count, condition_mix, highlight_title
--   FROM dbo.v_pallets WHERE pallet_number = 90;
--
-- Rollback notes:
--   Code: revert the merge commit on main (SWA redeploys the previous build).
--   These DB changes are additive and safe to leave in place with the old
--   code (old code ignores new columns; items_with_cost still exists; the old
--   v_public_pallets exclusion simply stops applying if you re-run the
--   previous view script). To fully reverse:
--
--   -- views back to the square-invoices.sql / wishlist3-part2.sql definitions:
--   --   re-run db/square-invoices.sql (v_pallets) then the v_public_pallets
--   --   block of db/wishlist3-part2.sql
--   -- proc back to the wishlist2-part2.sql definition:
--   --   re-run the sp_SetPublishState block of db/wishlist2-part2.sql
--   DROP PROCEDURE IF EXISTS dbo.sp_SoldToInventory;
--   DROP PROCEDURE IF EXISTS dbo.sp_RegisterMember;
--   DROP TABLE IF EXISTS dbo.manifest_history;   -- loses the audit trail
--   DROP TABLE IF EXISTS dbo.members;            -- loses signups: export /api/members/export.csv FIRST
--   ALTER TABLE dbo.line_items DROP CONSTRAINT DF_line_items_is_highlight;
--   ALTER TABLE dbo.line_items DROP COLUMN is_highlight;
--   ALTER TABLE dbo.manifests DROP CONSTRAINT CK_manifests_box_size;
--   ALTER TABLE dbo.manifests DROP COLUMN box_size, weight_lbs, live_at, sold_to_inventory_at;
--
--   Fake-sold originals that were marked SOLD keep publish_state='sold' after
--   a rollback; their clones remain as Draft boxes — both are ordinary rows.
-- ============================================================================

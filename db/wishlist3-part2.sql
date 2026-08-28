-- ----------------------------------------------------------------------------
-- Wishlist 3, part 2 (Rob's 2026-08-27 list, batch 2)
--
-- public_description: box-level, PUBLIC-facing blurb ("witty commentary")
-- shown on the website under the box + pricing. Distinct from:
--   * manifests.notes            — internal staff notes (never public)
--   * line_items.description     — per-item product description
--
-- Flows admin PATCH -> manifests -> v_pallets (admin) -> v_public_pallets
-- (homepage rows + manifest modal).
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.manifests', 'public_description') IS NULL
    ALTER TABLE dbo.manifests ADD public_description NVARCHAR(MAX) NULL;
GO

-- v_pallets: same OUTER APPLY definition as wishlist3-quickwins + public_description.
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
    agg.item_count,
    agg.unit_count,
    agg.total_msrp,
    agg.total_cost_units,
    agg.total_wholesale,
    agg.total_est_resale,
    agg.items_enriched,
    agg.items_with_cost
FROM dbo.manifests m
OUTER APPLY (
    SELECT
        COUNT(li.id)                      AS item_count,
        SUM(li.qty)                       AS unit_count,
        SUM(li.est_msrp * li.qty)         AS total_msrp,
        SUM(li.unit_cost * li.qty)        AS total_cost_units,
        SUM(li.wholesale_price * li.qty)  AS total_wholesale,
        SUM(li.est_resale * li.qty)       AS total_est_resale,
        SUM(CASE WHEN li.enrich_status = 'hit'   THEN 1 ELSE 0 END) AS items_enriched,
        SUM(CASE WHEN li.unit_cost IS NOT NULL   THEN 1 ELSE 0 END) AS items_with_cost
    FROM dbo.line_items li
    WHERE li.manifest_id = m.id
) agg;
GO
GRANT SELECT ON dbo.v_pallets TO nsl_api;

-- v_public_pallets: wishlist2-part2 definition + public_description.
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
    p.photo_url,
    p.public_description,
    p.item_count,
    p.unit_count,
    p.total_msrp,
    p.list_price,
    p.sale_price,
    CAST(CASE WHEN p.publish_state IN ('ghost','sold') THEN 1 ELSE 0 END AS BIT) AS is_sold,
    CAST(CASE WHEN p.sale_price IS NOT NULL AND p.list_price IS NOT NULL
                   AND p.sale_price < p.list_price
              THEN 1 ELSE 0 END AS BIT) AS is_on_sale,
    COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p
WHERE p.archived_at IS NULL
  AND p.publish_state IN ('live','ghost','sold');
GO
GRANT SELECT ON dbo.v_public_pallets TO nsl_api;

PRINT 'wishlist3-part2: public_description added; views rebuilt.';

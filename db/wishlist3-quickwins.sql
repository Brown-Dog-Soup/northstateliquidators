-- ----------------------------------------------------------------------------
-- Wishlist 3 quick wins (Rob's 2026-08-27 "Update Requests" email)
--
-- v_pallets rebuild with two additions:
--   * m.notes  — THE notes-disappearing bug: the admin detail view loads the
--     pallet from v_pallets, which never exposed notes, so the Notes box always
--     rendered empty and the next Save wrote '' back to dbo.manifests, wiping
--     whatever was there. Exposing notes fixes both display and the wipe.
--   * items_with_cost — count of line items with a defined unit_cost, so the
--     admin card can warn "3/50 items have costs" before someone prices a box
--     off a partial roll-up.
--
-- The aggregate moves from GROUP BY to OUTER APPLY because notes is
-- NVARCHAR(MAX) (not groupable). Same output rows/columns as before, plus the
-- two new ones. v_public_pallets reads v_pallets by name and resolves at
-- runtime, so it is unaffected by the drop/create.
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

PRINT 'wishlist3-quickwins: v_pallets now exposes notes + items_with_cost.';

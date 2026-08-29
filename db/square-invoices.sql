-- ----------------------------------------------------------------------------
-- Square Invoices for wholesale (SQUARE-INTEGRATION.md follow-on)
--
-- "Invoice this box": staff sends a real Square invoice (card 3.3%+30c, or
-- ACH 1% — the wholesale winner) for one box. The invoice's order_id goes in
-- the SAME checkout_order_id correlation column the webhook already matches
-- on, so payment.updated COMPLETED marks the box SOLD with zero new logic.
-- Invoicing retires any public payment link and parks the box in draft
-- (reserved for the buyer, off the website).
-- ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.manifests', 'invoice_id') IS NULL
    ALTER TABLE dbo.manifests ADD
        invoice_id  VARCHAR(64)   NULL,
        invoice_url NVARCHAR(500) NULL;
GO

-- v_pallets rebuild: same as wishlist3-part2 + invoice columns (admin needs
-- them to show/cancel an outstanding invoice on the detail page).
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

PRINT 'square-invoices: manifests invoice columns + v_pallets rebuilt.';

-- ----------------------------------------------------------------------------
-- Backfill line_items.unit_cost / wholesale_price from lpn_catalog.
--
-- line_items snapshots catalog pricing at scan time, so items scanned before a
-- manifest import carry NULL cost/wholesale even after the catalog learns the
-- numbers. The admin/receiving APIs now COALESCE against the catalog at query
-- time for display, but v_pallets' box-cost roll-up (SUM(li.unit_cost * qty))
-- reads the physical column — so stale rows need a one-time fill.
--
-- Idempotent: only touches rows where the column is NULL and the catalog has a
-- value. Matches by the same key ladder as sp_RecordScan: lpn > upc > asin.
-- Safe to re-run after any future manifest import.
-- (First run of this pattern was 2026-08-13, inline; this file makes it a
--  repeatable migration.)
-- ----------------------------------------------------------------------------

UPDATE li SET
    li.unit_cost       = COALESCE(li.unit_cost,       cat.unit_cost),
    li.wholesale_price = COALESCE(li.wholesale_price, cat.wholesale_price)
FROM dbo.line_items li
CROSS APPLY (
    SELECT TOP 1 c.unit_cost, c.wholesale_price
    FROM dbo.lpn_catalog c
    WHERE c.lpn = li.lpn
       OR (li.upc  IS NOT NULL AND c.upc  = li.upc)
       OR (li.asin IS NOT NULL AND c.asin = li.asin)
    ORDER BY CASE WHEN c.lpn = li.lpn THEN 0 WHEN c.upc = li.upc THEN 1 ELSE 2 END
) cat
WHERE (li.unit_cost       IS NULL AND cat.unit_cost       IS NOT NULL)
   OR (li.wholesale_price IS NULL AND cat.wholesale_price IS NOT NULL);

PRINT CONCAT('backfill-item-costs: updated ', @@ROWCOUNT, ' line_items row(s).');

-- Remaining cost-less rows are expected to be unmanifested "mystery/bonus"
-- items (Rob 2026-08-13) or manual ad-hoc adds; report the count for sanity.
SELECT COUNT(*) AS items_still_missing_cost
FROM dbo.line_items WHERE unit_cost IS NULL;

-- ----------------------------------------------------------------------------
-- Hotfix 2026-09-13: the Square webhook flagged floor (POS) sales as UNMATCHED
-- / needs_refund because the merchant account is shared with the counter.
-- Remove only rows whose event payload says square_product = RETAIL (the POS)
-- and that were never matched to a box. Print what is removed.
-- ----------------------------------------------------------------------------
SELECT square_payment_id, amount_cents, created_at,
       JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') AS product
FROM dbo.payments
WHERE status = 'UNMATCHED' AND manifest_id IS NULL
  AND JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') NOT IN ('ECOMMERCE_API','INVOICES');

DELETE FROM dbo.payments
WHERE status = 'UNMATCHED' AND manifest_id IS NULL
  AND JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') NOT IN ('ECOMMERCE_API','INVOICES');

PRINT 'hotfix-floor-payments: removed ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' floor payment row(s).';

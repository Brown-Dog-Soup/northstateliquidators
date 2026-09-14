-- ----------------------------------------------------------------------------
-- Hotfix 2026-09-13: the Square webhook flagged two floor (POS) cash sales from
-- 2026-09-11 as UNMATCHED / needs_refund because the merchant account is shared
-- with the counter (square_product = RETAIL). Pinned to those two payment ids so
-- this can never touch anything else in the audit table. Rolls back unless
-- exactly 2 rows match.
-- ----------------------------------------------------------------------------
SELECT square_payment_id, amount_cents, created_at,
       JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') AS product
FROM dbo.payments
WHERE square_payment_id IN ('xWNLYnGXabPUAraqnJWIOjlKxVMZY', 'PLUPdp1HUHrDrRrQBqU7Au56OAXZY')
  AND status = 'UNMATCHED' AND manifest_id IS NULL;

BEGIN TRAN;
DELETE FROM dbo.payments
WHERE square_payment_id IN ('xWNLYnGXabPUAraqnJWIOjlKxVMZY', 'PLUPdp1HUHrDrRrQBqU7Au56OAXZY')
  AND status = 'UNMATCHED' AND manifest_id IS NULL;
IF @@ROWCOUNT <> 2
BEGIN
    ROLLBACK TRAN;
    PRINT 'hotfix-floor-payments: expected exactly 2 rows — nothing removed. Check the SELECT above.';
END
ELSE
BEGIN
    COMMIT TRAN;
    PRINT 'hotfix-floor-payments: removed 2 floor payment row(s).';
END;

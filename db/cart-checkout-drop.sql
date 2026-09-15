-- ============================================================================
-- Cart + combined checkout — phase 2. Drop legacy manifests.checkout_* columns.
-- DESTRUCTIVE. Apply ONLY after the cart code has been in production for
-- several days AND the preconditions below pass. This is the one step in the
-- entire build that cannot be undone by redeploying code.
-- ============================================================================
--
-- OPERATOR PRECONDITION: Before applying this script, run the grep gate below.
-- If it returns any .cs files, abort — do not apply this script. Matches in
-- .sql files, docs/, and task briefs are expected and are not blockers.
--
--   grep -rn "checkout_order_id\|checkout_link_id\|checkout_url\|checkout_created_at" \
--     api/ --include=*.cs
--
-- At the time this script was written (2026-09-13), these columns were still
-- actively referenced in:
--   - api/Functions/PalletsFunction.cs (SoldToInventory path)
--   - api/Functions/SquareFunction.cs (invoice + reconcile paths)
--
-- Tasks 6 and 7 of this build rewrite both functions to use dbo.checkout_orders /
-- dbo.checkout_order_boxes instead, removing all .cs references before the cart
-- goes live. Once Tasks 6–7 are complete, the grep above will return only .sql
-- and docs, and this script's precheck (below) becomes the final gate.
--
-- ============================================================================
SET NOCOUNT ON;

-- Runtime precheck: refuse if any legacy order_id in manifests has not yet been
-- copied to dbo.checkout_orders. This ensures phase 1 (db/cart-checkout.sql) ran
-- and the copy settled before we drop the source columns.
IF EXISTS (SELECT 1 FROM dbo.manifests m WHERE m.checkout_order_id IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM dbo.checkout_orders o WHERE o.square_order_id = m.checkout_order_id))
BEGIN
    RAISERROR('cart-checkout-drop: manifests still holds order ids not copied to checkout_orders — re-run db/cart-checkout.sql first.', 16, 1);
    RETURN;
END;

-- Drop the four legacy columns, guarded so re-runs are safe.
IF COL_LENGTH('dbo.manifests', 'checkout_link_id')    IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_link_id;
IF COL_LENGTH('dbo.manifests', 'checkout_order_id')   IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_order_id;
IF COL_LENGTH('dbo.manifests', 'checkout_url')        IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_url;
IF COL_LENGTH('dbo.manifests', 'checkout_created_at') IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_created_at;
GO

PRINT 'cart-checkout-drop: legacy manifests.checkout_* columns removed.';

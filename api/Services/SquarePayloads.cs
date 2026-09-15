using System.Security.Cryptography;
using System.Text;

namespace NSL.Api.Services;

/// <summary>One box on a cart checkout. ManifestId becomes the Square line-item uid.</summary>
public sealed record CartLine(Guid ManifestId, string Name, long AmountCents);

/// <summary>How the buyer gets the boxes (spec §8.3). Collected in our cart
/// drawer, not by Square — the hosted page has no three-way choice.</summary>
public enum DeliveryMethod { Pickup, Delivery, Flea }

public static class DeliveryMethods
{
    public static string ToDb(DeliveryMethod m) => m switch
    {
        DeliveryMethod.Delivery => "delivery",
        DeliveryMethod.Flea => "flea",
        _ => "pickup"
    };

    /// <summary>Anything unrecognised (including null) falls back to pickup — the free, safe default.</summary>
    public static bool TryParse(string? s, out DeliveryMethod m)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "": case "pickup": m = DeliveryMethod.Pickup; return true;
            case "delivery":        m = DeliveryMethod.Delivery; return true;
            case "flea":            m = DeliveryMethod.Flea; return true;
            default:                m = DeliveryMethod.Pickup; return false;
        }
    }
}

/// <summary>
/// Pure builders for the Square request bodies the cart needs. Kept free of
/// I/O so they are unit-testable; SquareService serializes and posts them.
/// Limits (Square docs, verified 2026-09-13): line name ≤512, line uid ≤60,
/// payment_note ≤500, redirect_url ≤2048, refund idempotency_key ≤45.
/// </summary>
public static class SquarePayloads
{
    public const string PickupFieldTitle = "Name & phone for pickup";

    // Tax (spec §8.1). 7.25% = 4.75% NC + 2.00% Wake County + 0.50% transit.
    public const string TaxUid     = "NC-SALES-725";
    public const string TaxName    = "NC sales tax (7.25%)";
    public const string TaxPercent = "7.25";               // Square wants a STRING

    // Delivery (spec §8.2). Sent as an order service charge, never as
    // checkout_options.shipping_fee — Square materialises that one with
    // "taxable": false and no applied_taxes, and NC taxes a delivery charge.
    public const string DeliveryUid  = "NSL-DELIVERY";
    public const string DeliveryName = "Local delivery (within 20 miles)";
    public const long   DeliveryCents = 1000;

    /// <summary>Buyer-visible note on each box line — names the handover they picked.</summary>
    public static string LineNote(DeliveryMethod d) => d switch
    {
        DeliveryMethod.Delivery => "Local delivery — we'll call to schedule",
        DeliveryMethod.Flea     => "Friday pickup at the Raleigh Flea Market",
        _                       => "Pickup in Wake Forest, NC"
    };

    public static object CartLink(IReadOnlyList<CartLine> lines, string locationId, string redirectUrl,
        string idempotencyKey, string referenceId, string paymentNote, string supportEmail,
        DeliveryMethod delivery, string? taxCatalogId)
    {
        var note = LineNote(delivery);
        var order = new Dictionary<string, object?>
        {
            ["location_id"] = locationId,
            ["reference_id"] = Cap(referenceId, 40),   // backstop only — the caller keeps it inside 40
            ["line_items"] = lines.Select(l => new
            {
                uid = l.ManifestId.ToString(),
                name = Cap(l.Name, 512),
                quantity = "1",
                base_price_money = new { amount = l.AmountCents, currency = "USD" },
                applied_taxes = new[] { new { tax_uid = TaxUid } },
                note
            }).ToArray(),
            // ONE tax object → the buyer sees one tax line, not one per box.
            // LINE_ITEM scope (not ORDER) is what lets the delivery service
            // charge reference it: an ORDER-scope tax is only spread across
            // line items and never reaches a service charge.
            //
            // Two shapes, same uid either way (amendment 2026-09-15):
            //   catalog — the account's own "NC & Wake County Sales Tax" object,
            //             so name/percentage/type come from Square and the buyer
            //             sees the merchant's real tax line. Sending those fields
            //             alongside catalog_object_id is how you get two lines.
            //   ad-hoc  — fallback when SQUARE_TAX_CATALOG_ID isn't configured.
            ["taxes"] = new object[]
            {
                string.IsNullOrEmpty(taxCatalogId)
                    ? new { uid = TaxUid, name = TaxName, percentage = TaxPercent, type = "ADDITIVE", scope = "LINE_ITEM" }
                    : (object)new { uid = TaxUid, catalog_object_id = taxCatalogId, scope = "LINE_ITEM" }
            }
        };

        if (delivery == DeliveryMethod.Delivery)
            order["service_charges"] = new[]
            {
                new
                {
                    uid = DeliveryUid,
                    name = DeliveryName,
                    amount_money = new { amount = DeliveryCents, currency = "USD" },
                    calculation_phase = "SUBTOTAL_PHASE",   // before tax, so NC's tax lands on it
                    scope = "ORDER",
                    treatment_type = "LINE_ITEM_TREATMENT",
                    taxable = true,                          // documentation-only; applied_taxes does the work
                    applied_taxes = new[] { new { tax_uid = TaxUid } }
                }
            };

        return new
        {
            idempotency_key = idempotencyKey,
            order,
            checkout_options = new
            {
                redirect_url = redirectUrl,
                enable_coupon = false,
                allow_tipping = false,
                merchant_support_email = supportEmail,
                custom_fields = new[] { new { title = PickupFieldTitle } }
            },
            payment_note = Cap(paymentNote, 500)
        };
    }

    public static string BoxLineName(int palletNumber, string? displayName)
        => Cap($"BOX #{palletNumber} — {(string.IsNullOrWhiteSpace(displayName) ? "NSL Box" : displayName)}", 512);

    public static string PaymentNote(IEnumerable<int> palletNumbers)
        => Cap("NSL boxes " + string.Join(", ", palletNumbers.Select(n => "#" + n)), 500);

    /// <summary>Square caps refund idempotency keys at 45 chars; payment ids
    /// alone can be longer than that, so hash (payment, amount) → 40 hex + prefix.</summary>
    public static string RefundKey(string paymentId, long amountCents)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(paymentId + ":" + amountCents));
        return "nslr-" + Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}

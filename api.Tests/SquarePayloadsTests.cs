using System.Text.Json;
using NSL.Api.Services;
using Xunit;

public class SquarePayloadsTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The live account's "NC &amp; Wake County Sales Tax" catalog object.</summary>
    private const string CatalogTaxId = "NJMJVQ3TQDEYCNQJJ5MGTCXT";

    private static JsonElement Build(params CartLine[] lines) => Build(DeliveryMethod.Pickup, lines);

    private static JsonElement Build(DeliveryMethod delivery, params CartLine[] lines)
        => Build(null, delivery, lines);

    private static JsonElement Build(string? taxCatalogId, DeliveryMethod delivery, params CartLine[] lines)
    {
        var payload = SquarePayloads.CartLink(lines, "LOC1", "https://x/thanks.html?boxes=1,2",
            "nsl-cart-abc", "NSL #1, #2", "NSL boxes #1, #2", "hello@example.com", delivery, taxCatalogId);
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    }

    [Fact]
    public void CartLink_uses_order_not_quick_pay_and_one_line_per_box()
    {
        var root = Build(new CartLine(A, "BOX #1 — Tools", 18000), new CartLine(B, "BOX #2 — Toys", 25000));
        Assert.False(root.TryGetProperty("quick_pay", out _));
        var order = root.GetProperty("order");
        Assert.Equal("LOC1", order.GetProperty("location_id").GetString());
        Assert.Equal("NSL #1, #2", order.GetProperty("reference_id").GetString());
        var items = order.GetProperty("line_items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(A.ToString(), items[0].GetProperty("uid").GetString());
        Assert.Equal("1", items[0].GetProperty("quantity").GetString());
        Assert.Equal(18000, items[0].GetProperty("base_price_money").GetProperty("amount").GetInt64());
        Assert.Equal("USD", items[0].GetProperty("base_price_money").GetProperty("currency").GetString());
        Assert.Equal("BOX #2 — Toys", items[1].GetProperty("name").GetString());
        Assert.Equal("nsl-cart-abc", root.GetProperty("idempotency_key").GetString());
        Assert.Equal("NSL boxes #1, #2", root.GetProperty("payment_note").GetString());
    }

    [Fact]
    public void CartLink_disables_coupons_and_tips_and_sets_redirect_and_support_email()
    {
        var co = Build(new CartLine(A, "BOX #1", 100)).GetProperty("checkout_options");
        Assert.False(co.GetProperty("enable_coupon").GetBoolean());
        Assert.False(co.GetProperty("allow_tipping").GetBoolean());
        Assert.Equal("https://x/thanks.html?boxes=1,2", co.GetProperty("redirect_url").GetString());
        Assert.Equal("hello@example.com", co.GetProperty("merchant_support_email").GetString());
        Assert.False(co.TryGetProperty("ask_for_shipping_address", out _));
        var cf = co.GetProperty("custom_fields");
        Assert.Equal(1, cf.GetArrayLength());
        Assert.Equal("Name & phone for pickup", cf[0].GetProperty("title").GetString());
    }

    // ── tax + delivery (spec §8.1, §8.2) ──────────────────────────────────

    [Fact]
    public void Order_carries_one_additive_line_item_scope_tax_at_725()
    {
        var order = Build(new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000)).GetProperty("order");
        var taxes = order.GetProperty("taxes");
        Assert.Equal(1, taxes.GetArrayLength());
        Assert.Equal("NC-SALES-725", taxes[0].GetProperty("uid").GetString());
        Assert.Equal("NC sales tax (7.25%)", taxes[0].GetProperty("name").GetString());
        Assert.Equal("7.25", taxes[0].GetProperty("percentage").GetString());   // string, not number
        Assert.Equal("ADDITIVE", taxes[0].GetProperty("type").GetString());
        // LINE_ITEM, not ORDER: an ORDER-scope tax never reaches a service charge.
        Assert.Equal("LINE_ITEM", taxes[0].GetProperty("scope").GetString());
    }

    [Fact]
    public void Every_box_line_applies_the_tax()
    {
        var items = Build(new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000))
            .GetProperty("order").GetProperty("line_items");
        foreach (var li in items.EnumerateArray())
        {
            var at = li.GetProperty("applied_taxes");
            Assert.Equal(1, at.GetArrayLength());
            Assert.Equal("NC-SALES-725", at[0].GetProperty("tax_uid").GetString());
        }
    }

    [Theory]
    [InlineData(DeliveryMethod.Pickup)]
    [InlineData(DeliveryMethod.Flea)]
    public void No_service_charge_when_there_is_no_delivery(DeliveryMethod m)
    {
        var order = Build(m, new CartLine(A, "BOX #1", 18000)).GetProperty("order");
        Assert.False(order.TryGetProperty("service_charges", out _));
    }

    [Fact]
    public void Delivery_adds_one_taxed_subtotal_phase_service_charge()
    {
        var order = Build(DeliveryMethod.Delivery, new CartLine(A, "BOX #1", 18000)).GetProperty("order");
        var sc = order.GetProperty("service_charges");
        Assert.Equal(1, sc.GetArrayLength());
        Assert.Equal("NSL-DELIVERY", sc[0].GetProperty("uid").GetString());
        Assert.Equal(1000, sc[0].GetProperty("amount_money").GetProperty("amount").GetInt64());
        Assert.Equal("USD", sc[0].GetProperty("amount_money").GetProperty("currency").GetString());
        // SUBTOTAL_PHASE = before tax, so NC's tax on the delivery charge lands.
        Assert.Equal("SUBTOTAL_PHASE", sc[0].GetProperty("calculation_phase").GetString());
        Assert.Equal("ORDER", sc[0].GetProperty("scope").GetString());
        Assert.Equal("LINE_ITEM_TREATMENT", sc[0].GetProperty("treatment_type").GetString());
        Assert.True(sc[0].GetProperty("taxable").GetBoolean());
        // taxable alone does nothing — applied_taxes is what charges the tax.
        Assert.Equal("NC-SALES-725", sc[0].GetProperty("applied_taxes")[0].GetProperty("tax_uid").GetString());
    }

    [Fact]
    public void Delivery_order_carries_the_tax_and_every_line_references_it()
    {
        // This test used to assert the ABSENCE of shipping_fee /
        // ask_for_shipping_address on a checkout_options literal that never had
        // those keys, so it could not fail. These assertions break if the tax
        // wiring is removed, which is what we actually need guarded.
        var order = Build(DeliveryMethod.Delivery, new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000))
            .GetProperty("order");
        var taxes = order.GetProperty("taxes");
        Assert.Equal(1, taxes.GetArrayLength());
        Assert.Equal(SquarePayloads.TaxUid, taxes[0].GetProperty("uid").GetString());
        foreach (var li in order.GetProperty("line_items").EnumerateArray())
            Assert.Equal(SquarePayloads.TaxUid, li.GetProperty("applied_taxes")[0].GetProperty("tax_uid").GetString());
    }

    // ── catalog tax object (amendment 2026-09-15) ─────────────────────────

    [Fact]
    public void Catalog_tax_sends_the_object_id_and_none_of_the_ad_hoc_fields()
    {
        var taxes = Build(CatalogTaxId, DeliveryMethod.Pickup, new CartLine(A, "BOX #1", 18000))
            .GetProperty("order").GetProperty("taxes");
        Assert.Equal(JsonValueKind.Array, taxes.ValueKind);
        Assert.Equal(1, taxes.GetArrayLength());
        Assert.Equal(SquarePayloads.TaxUid, taxes[0].GetProperty("uid").GetString());
        Assert.Equal(CatalogTaxId, taxes[0].GetProperty("catalog_object_id").GetString());
        // Scope stays LINE_ITEM so the delivery service charge can reference it.
        Assert.Equal("LINE_ITEM", taxes[0].GetProperty("scope").GetString());
        // name / percentage / type come from the catalog object. Sending them as
        // well is how you end up with two tax lines on the buyer's page.
        Assert.False(taxes[0].TryGetProperty("percentage", out _));
        Assert.False(taxes[0].TryGetProperty("name", out _));
        Assert.False(taxes[0].TryGetProperty("type", out _));
    }

    [Theory]
    [InlineData(null)]           // ad-hoc shape
    [InlineData(CatalogTaxId)]   // catalog shape
    public void Tax_uid_is_identical_on_the_order_tax_every_line_and_the_delivery_charge(string? taxCatalogId)
    {
        // If these three drift apart the tax silently applies to nothing.
        var order = Build(taxCatalogId, DeliveryMethod.Delivery,
            new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000)).GetProperty("order");

        var taxes = order.GetProperty("taxes");
        Assert.Equal(JsonValueKind.Array, taxes.ValueKind);
        Assert.Equal(1, taxes.GetArrayLength());
        var uid = taxes[0].GetProperty("uid").GetString();
        Assert.Equal(SquarePayloads.TaxUid, uid);

        foreach (var li in order.GetProperty("line_items").EnumerateArray())
        {
            var at = li.GetProperty("applied_taxes");
            Assert.Equal(JsonValueKind.Array, at.ValueKind);
            Assert.Equal(1, at.GetArrayLength());
            Assert.Equal(uid, at[0].GetProperty("tax_uid").GetString());
        }

        var charges = order.GetProperty("service_charges");
        Assert.Equal(JsonValueKind.Array, charges.ValueKind);
        Assert.Equal(1, charges.GetArrayLength());
        var scTaxes = charges[0].GetProperty("applied_taxes");
        Assert.Equal(JsonValueKind.Array, scTaxes.ValueKind);
        Assert.Equal(1, scTaxes.GetArrayLength());
        Assert.Equal(uid, scTaxes[0].GetProperty("tax_uid").GetString());
    }

    [Theory]
    [InlineData(DeliveryMethod.Pickup, "Pickup in Wake Forest, NC")]
    [InlineData(DeliveryMethod.Delivery, "Local delivery — we'll call to schedule")]
    [InlineData(DeliveryMethod.Flea, "Friday pickup at the Raleigh Flea Market")]
    public void Line_note_names_the_chosen_handover(DeliveryMethod m, string expected)
    {
        var items = Build(m, new CartLine(A, "BOX #1", 18000)).GetProperty("order").GetProperty("line_items");
        Assert.Equal(expected, items[0].GetProperty("note").GetString());
    }

    [Fact]
    public void BoxLineName_is_capped_at_512()
    {
        Assert.Equal("BOX #12 — Tools", SquarePayloads.BoxLineName(12, "Tools"));
        Assert.Equal("BOX #12 — NSL Box", SquarePayloads.BoxLineName(12, null));
        Assert.Equal(512, SquarePayloads.BoxLineName(12, new string('x', 600)).Length);
    }

    [Fact]
    public void PaymentNote_lists_boxes_and_is_capped_at_500()
    {
        Assert.Equal("NSL boxes #12, #14", SquarePayloads.PaymentNote(new[] { 12, 14 }));
        Assert.True(SquarePayloads.PaymentNote(Enumerable.Range(100000, 200)).Length <= 500);
    }

    [Fact]
    public void RefundKey_is_deterministic_45_chars_and_amount_sensitive()
    {
        var k1 = SquarePayloads.RefundKey("FAKE-PAYMENT-ID-FOR-TESTS", 25000);
        var k2 = SquarePayloads.RefundKey("FAKE-PAYMENT-ID-FOR-TESTS", 25000);
        var k3 = SquarePayloads.RefundKey("FAKE-PAYMENT-ID-FOR-TESTS", 100);
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.Equal(45, k1.Length);
        Assert.StartsWith("nslr-", k1);
        Assert.Equal(45, SquarePayloads.RefundKey(new string('p', 192), 1).Length);
    }
}

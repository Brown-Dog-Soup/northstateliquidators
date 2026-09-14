using System.Text.Json;

namespace NSL.Api.Services;

public sealed record SquarePaymentEvent(string PaymentId, string? Status, string? OrderId, long? AmountCents, string? Product);
public sealed record SquareRefundEvent(string RefundId, string? Status, string? PaymentId, long? AmountCents);

/// <summary>
/// Defensive readers for Square webhook payloads. Everything is TryGet —
/// a malformed or unexpected event must never throw (a 500 makes Square
/// retry ~11 times over 24h). Also the one place that decides whether a
/// payment is "ours": the merchant account is shared with the floor POS,
/// so a payment.updated for a cash sale at the counter arrives here too.
/// </summary>
public static class SquareEvents
{
    /// <summary>application_details.square_product values produced by our
    /// payment links (ECOMMERCE_API) and wholesale invoices (INVOICES).</summary>
    private static readonly HashSet<string> OurProducts =
        new(StringComparer.OrdinalIgnoreCase) { "ECOMMERCE_API", "INVOICES" };

    public static bool IsOurProduct(string? product)
        => !string.IsNullOrEmpty(product) && OurProducts.Contains(product);

    public static string? EventType(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) &&
           t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    public static bool TryParsePayment(JsonElement root, out SquarePaymentEvent ev)
    {
        ev = null!;
        if (!TryObject(root, "data", out var data) || !TryObject(data, "object", out var obj) ||
            !TryObject(obj, "payment", out var payment))
            return false;
        var id = Str(payment, "id");
        if (string.IsNullOrEmpty(id)) return false;
        string? product = TryObject(payment, "application_details", out var app) ? Str(app, "square_product") : null;
        ev = new SquarePaymentEvent(id, Str(payment, "status"), Str(payment, "order_id"), Money(payment, "amount_money"), product);
        return true;
    }

    public static bool TryParseRefund(JsonElement root, out SquareRefundEvent ev)
    {
        ev = null!;
        if (!TryObject(root, "data", out var data) || !TryObject(data, "object", out var obj) ||
            !TryObject(obj, "refund", out var refund))
            return false;
        var id = Str(refund, "id");
        if (string.IsNullOrEmpty(id)) return false;
        ev = new SquareRefundEvent(id, Str(refund, "status"), Str(refund, "payment_id"), Money(refund, "amount_money"));
        return true;
    }

    private static bool TryObject(JsonElement el, string name, out JsonElement child)
    {
        child = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out child) &&
               child.ValueKind == JsonValueKind.Object;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Money(JsonElement el, string name)
        => TryObject(el, name, out var m) && m.TryGetProperty("amount", out var a) &&
           a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var n) ? n : null;
}

namespace NSL.Api.Services;

/// <summary>
/// The ONE definition of "can this order still sell this box" (spec §4).
/// A public cart link may only sell a box that is live, not archived, not a
/// ghost and not reserved by an outstanding wholesale invoice. An invoice
/// order may sell its (drafted) box as long as nobody else sold it first.
/// </summary>
public static class Availability
{
    public static bool ForLink(string? publishState, DateTime? archivedAt, bool isGhost, string? invoiceId)
        => publishState == "live" && archivedAt == null && !isGhost && invoiceId == null;

    public static bool ForInvoice(string? publishState) => publishState != "sold";

    public static bool For(string kind, string? publishState, DateTime? archivedAt, bool isGhost, string? invoiceId)
        => kind == "invoice" ? ForInvoice(publishState) : ForLink(publishState, archivedAt, isGhost, invoiceId);
}

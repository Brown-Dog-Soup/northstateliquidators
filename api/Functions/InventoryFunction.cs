using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// GET /api/inventory
///
/// Returns every lpn_catalog row joined to its current assignment status
/// (Available / On Pallet / Individual / Sold / Ghost / Archived). Powers the
/// /staff/inventory page.
///
/// Query params:
///   ?status=available|on_pallet|individual|sold|ghost|archived  (optional filter)
///   ?lot=<lot_id-or-source_pallet_ref>                          (optional filter)
///   ?q=<search>                                                  (optional, matches title/brand/upc/lpn)
///   ?limit=<n>   default 500
/// </summary>
public sealed class InventoryFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<InventoryFunction> _log;

    public InventoryFunction(SqlService sql, ILogger<InventoryFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("ListInventory")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory")] HttpRequest req,
        CancellationToken ct)
    {
        var status = req.Query["status"].ToString();
        var lot    = req.Query["lot"].ToString();
        var q      = req.Query["q"].ToString();
        if (!int.TryParse(req.Query["limit"].ToString(), out var limit) || limit <= 0 || limit > 5000)
            limit = 500;

        var where = new List<string>();
        var p = new DynamicParameters();
        p.Add("Limit", limit);

        if (!string.IsNullOrWhiteSpace(status)) { where.Add("status = @Status"); p.Add("Status", status); }
        // Lot filter matches ANY of the B-Stock grouping identifiers the owners
        // group by: Order #, Amazon Lot ID, or Pallet ID (source_pallet_ref also
        // carries the Pallet ID for B-Stock rows). Pick one in the dropdown, then
        // Select-all → Create pallet.
        if (!string.IsNullOrWhiteSpace(lot))
        {
            where.Add("(order_number = @Lot OR lot_id = @Lot OR source_pallet_id = @Lot OR source_pallet_ref = @Lot)");
            p.Add("Lot", lot);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Free-text search also matches the lot identifiers, so staff can just
            // type "AMZ0N-OJ5-4G8R" or a pallet id to pull up a whole group.
            where.Add(@"(title LIKE @Q OR brand LIKE @Q OR upc LIKE @Q OR lpn LIKE @Q
                        OR order_number LIKE @Q OR lot_id LIKE @Q
                        OR source_pallet_id LIKE @Q OR source_pallet_ref LIKE @Q)");
            p.Add("Q", $"%{q}%");
        }

        var sql = $@"
SELECT TOP (@Limit)
    lpn, upc, asin, title, brand, category,
    catalog_condition, msrp, unit_cost, wholesale_price,
    qty_in_manifest, available_qty, allocated_qty,
    lot_id, order_number, source_pallet_ref, source_pallet_id, imported_at,
    line_item_id, assigned_pallet_id, assigned_pallet_name, assigned_pallet_number,
    assigned_sell_mode, assigned_pallet_is_ghost,
    scanned_qty, scanned_at, sold_at,
    status
FROM dbo.v_inventory
{(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
ORDER BY imported_at DESC, lpn";

        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(sql, p)).ToList();
        _log.LogInformation("Inventory: {N} rows (status={S} lot={L} q={Q})", rows.Count, status, lot, q);
        return new OkObjectResult(rows);
    }

    /// <summary>
    /// GET /api/inventory/summary — totals + status breakdown + lot list. Cheap
    /// query the inventory page can use to render the top-of-page summary cards
    /// without paging through every row.
    /// </summary>
    [Function("InventorySummary")]
    public async Task<IActionResult> Summary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory/summary")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);

        var byStatus = (await conn.QueryAsync(
            "SELECT status, COUNT(*) AS n FROM dbo.v_inventory GROUP BY status")).ToList();
        var totals = await conn.QueryFirstOrDefaultAsync(@"
SELECT
    COUNT(*) AS total_rows,
    SUM(CASE WHEN status = 'available' THEN 1 ELSE 0 END) AS available_rows,
    SUM(CASE WHEN status = 'on_pallet' OR status = 'individual' THEN 1 ELSE 0 END) AS assigned_rows,
    SUM(msrp)            AS total_msrp,
    SUM(unit_cost)       AS total_cost,
    SUM(wholesale_price) AS total_wholesale
FROM dbo.v_inventory");

        // Lot picker options across the three B-Stock grouping identifiers, each
        // labeled by type so a Pallet ID is distinguishable from an Order # or an
        // Amazon Lot in the dropdown.
        var lots = (await conn.QueryAsync(@"
SELECT TOP 60 lot, lot_type, n FROM (
    SELECT order_number     AS lot, 'B-Stock Order' AS lot_type, COUNT(*) AS n
        FROM dbo.v_inventory WHERE NULLIF(order_number,'')     IS NOT NULL GROUP BY order_number
    UNION ALL
    SELECT source_pallet_id AS lot, 'Pallet ID'     AS lot_type, COUNT(*) AS n
        FROM dbo.v_inventory WHERE NULLIF(source_pallet_id,'') IS NOT NULL GROUP BY source_pallet_id
    UNION ALL
    -- lot_id is overloaded: it's Amazon's Lot ID on B-Stock rows but the NSL
    -- Lot # on master-imported rows. Label each group by which it is — a group
    -- whose rows carry an Order # came from B-Stock (Amazon Lot); otherwise it's
    -- an NSL Lot # the owners attached.
    SELECT lot_id AS lot,
           CASE WHEN MAX(CASE WHEN NULLIF(order_number,'') IS NOT NULL THEN 1 ELSE 0 END) = 1
                THEN 'Amazon Lot' ELSE 'NSL Lot' END AS lot_type,
           COUNT(*) AS n
        FROM dbo.v_inventory WHERE NULLIF(lot_id,'') IS NOT NULL GROUP BY lot_id
) g
ORDER BY n DESC")).ToList();

        return new OkObjectResult(new { byStatus, totals, lots });
    }

    public sealed record AllocateRequest(string? lpn, int? qty, Guid? manifestId, string? newBoxName);

    /// <summary>
    /// POST /api/inventory/allocate — put a specific QUANTITY of an inventory
    /// item into a box (Rob's split-quantity ask). Body:
    ///   { lpn, qty, manifestId?, newBoxName? }
    /// manifestId picks an existing box; omit it to create a new one (named by
    /// newBoxName, or auto-numbered). Availability is total-on-hand minus what's
    /// already boxed, so the remainder stays available for other boxes.
    /// </summary>
    [Function("AllocateInventory")]
    public async Task<IActionResult> Allocate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inventory/allocate")] HttpRequest req,
        CancellationToken ct)
    {
        AllocateRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<AllocateRequest>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        if (string.IsNullOrWhiteSpace(body?.lpn))
            return new BadRequestObjectResult(new { error = "lpn is required" });
        if (!body!.qty.HasValue || body.qty.Value < 1)
            return new BadRequestObjectResult(new { error = "qty must be a positive whole number" });

        await using var conn = await _sql.OpenAsync(ct);
        try
        {
            var row = await conn.QueryFirstOrDefaultAsync(@"
EXEC dbo.sp_AllocateCatalogToBox
  @lpn = @Lpn, @qty = @Qty, @manifest_id = @Mid, @new_box_name = @Name",
                new { Lpn = body.lpn, Qty = body.qty.Value, Mid = body.manifestId, Name = body.newBoxName });

            if (row == null)
                return new ObjectResult(new { error = "allocation returned no rows" }) { StatusCode = 500 };

            _log.LogInformation("Allocate {Lpn} x{Qty} -> box {Box}", body.lpn, body.qty, (object?)row.manifest_id);
            return new OkObjectResult(row);
        }
        catch (SqlException ex)
        {
            // sp_AllocateCatalogToBox raises friendly messages (over-allocation,
            // unknown code, missing box) at severity 16 — surface them as 400s.
            _log.LogWarning("Allocate rejected: {Msg}", ex.Message);
            return new BadRequestObjectResult(new { error = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/inventory?lpn=...  — remove a catalog item, but only if it
    /// isn't on a pallet yet (no line items reference it). Returns 409 if any
    /// units have already been allocated to a box.
    /// </summary>
    [Function("DeleteInventoryItem")]
    public async Task<IActionResult> DeleteItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "inventory")] HttpRequest req,
        CancellationToken ct)
    {
        var lpn = req.Query["lpn"].ToString();
        if (string.IsNullOrWhiteSpace(lpn))
            return new BadRequestObjectResult(new { error = "lpn query parameter is required" });

        await using var conn = await _sql.OpenAsync(ct);

        var assigned = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM dbo.line_items WHERE lpn = @lpn", new { lpn });
        if (assigned > 0)
            return new ConflictObjectResult(new
            {
                error = "This item is already in a box — remove it from the box first.",
                assigned
            });

        var rows = await conn.ExecuteAsync(
            "DELETE FROM dbo.lpn_catalog WHERE lpn = @lpn", new { lpn });
        if (rows == 0) return new NotFoundResult();

        _log.LogInformation("DeleteInventoryItem {Lpn}: removed", lpn);
        return new OkObjectResult(new { lpn, deleted = true });
    }
}

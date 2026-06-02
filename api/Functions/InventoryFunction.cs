using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;

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
    qty_in_manifest, lot_id, order_number, source_pallet_ref, source_pallet_id, imported_at,
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
}

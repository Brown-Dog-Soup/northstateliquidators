using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;

namespace NSL.Api.Functions;

/// <summary>
/// GET /api/lookup/{code} — wraps sp_LookupCode for HTTP callers
/// (Power Apps fallback / future PWA / scriptable test).
/// Returns the catalog row if found; 404 with empty body if no match.
/// </summary>
public sealed class LookupFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<LookupFunction> _log;

    public LookupFunction(SqlService sql, ILogger<LookupFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("Lookup")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lookup/{code}")] HttpRequest req,
        string code,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new BadRequestObjectResult(new { error = "code path parameter is required" });

        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(
            "EXEC dbo.sp_LookupCode @code = @c",
            new { c = code });

        if (row == null) return new NotFoundResult();
        return new OkObjectResult(row);
    }
}

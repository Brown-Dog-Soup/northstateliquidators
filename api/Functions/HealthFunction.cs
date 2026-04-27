using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;

namespace NSL.Api.Functions;

/// <summary>
/// GET /api/health — returns 200 with a small JSON payload showing the API
/// is up and what it can reach. Useful smoke test post-deploy.
/// </summary>
public sealed class HealthFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<HealthFunction> _log;

    public HealthFunction(SqlService sql, ILogger<HealthFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("Health")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        CancellationToken ct)
    {
        var sqlOk = false;
        string? sqlError = null;
        try
        {
            await using var conn = await _sql.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            sqlOk = true;
        }
        catch (Exception ex)
        {
            sqlError = ex.Message;
            _log.LogError(ex, "Health check SQL probe failed");
        }

        return new OkObjectResult(new
        {
            status = sqlOk ? "ok" : "degraded",
            timestamp = DateTimeOffset.UtcNow,
            sql = new { ok = sqlOk, error = sqlError },
            version = "0.1.0"
        });
    }
}

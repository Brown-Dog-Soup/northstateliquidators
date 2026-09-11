using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// Member signups (Wishlist 4 F3) — lightweight capture, no login. A member
/// number ('2600001' = YY + 5 digits) is handed back on the public site and
/// given at the register. dbo.members is the future reseller row
/// (RESELLER-PROGRAM-DESIGN.md).
///
///   POST /api/public/register      — anonymous (honeypot + soft per-IP rate limit)
///   GET  /api/members              — staff list, newest first
///   GET  /api/members/export.csv   — staff CSV download for email blasts
/// </summary>
public sealed class MembersFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<MembersFunction> _log;

    public sealed record RegisterRequest(
        string? firstName, string? lastName, string? email, string? phone,
        string? city, string? state, string? zip, string? howHeard,
        string? website);   // honeypot — humans never see it; bots fill it

    // Soft per-IP rate limit: 5 signups per 60 s window, in-memory (per
    // Functions instance — scale-out resets it; fine for a signup form).
    private const int RateLimitPerWindow = 5;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (int count, DateTime windowStart)> _hits = new();

    private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex StateRx = new(@"^[A-Za-z]{2}$", RegexOptions.Compiled);

    public MembersFunction(SqlService sql, ILogger<MembersFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    private static string ClientIp(HttpRequest req)
    {
        var fwd = req.Headers["x-forwarded-for"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        return req.HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>True when this IP is over the limit for the current window.</summary>
    private static bool OverRateLimit(string ip)
    {
        var now = DateTime.UtcNow;
        var entry = _hits.AddOrUpdate(ip,
            _ => (1, now),
            (_, cur) => now - cur.windowStart >= RateWindow ? (1, now) : (cur.count + 1, cur.windowStart));
        // Opportunistic cleanup so the dictionary never grows unbounded.
        if (_hits.Count > 5000)
            foreach (var kv in _hits)
                if (now - kv.Value.windowStart >= RateWindow) _hits.TryRemove(kv.Key, out _);
        return entry.count > RateLimitPerWindow;
    }

    [Function("RegisterMember")]
    public async Task<IActionResult> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/register")] HttpRequest req,
        CancellationToken ct)
    {
        RegisterRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<RegisterRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        // 1. Honeypot: a filled "website" field is a bot. Pretend it worked, store nothing.
        if (!string.IsNullOrWhiteSpace(body?.website))
        {
            _log.LogInformation("RegisterMember: honeypot tripped from {Ip}", ClientIp(req));
            return new OkObjectResult(new { memberNumber = (string?)null, alreadyRegistered = false });
        }

        // 2. Soft per-IP rate limit.
        var ip = ClientIp(req);
        if (OverRateLimit(ip))
        {
            _log.LogWarning("RegisterMember: rate limit hit for {Ip}", ip);
            return new ObjectResult(new { error = "Too many signups from this connection — try again in a minute." }) { StatusCode = 429 };
        }

        // 3. Validation.
        var first = body?.firstName?.Trim() ?? "";
        var last  = body?.lastName?.Trim() ?? "";
        var email = body?.email?.Trim() ?? "";
        var phone = body?.phone?.Trim();
        var city  = body?.city?.Trim();
        var state = body?.state?.Trim();
        var zip   = body?.zip?.Trim();
        var how   = body?.howHeard?.Trim();

        if (first.Length == 0)  return new BadRequestObjectResult(new { error = "First name is required." });
        if (first.Length > 100) return new BadRequestObjectResult(new { error = "First name is too long (100 max)." });
        if (last.Length == 0)   return new BadRequestObjectResult(new { error = "Last name is required." });
        if (last.Length > 100)  return new BadRequestObjectResult(new { error = "Last name is too long (100 max)." });
        if (email.Length == 0)  return new BadRequestObjectResult(new { error = "Email is required." });
        if (email.Length > 320 || !EmailRx.IsMatch(email))
            return new BadRequestObjectResult(new { error = "That doesn't look like a valid email address." });
        if (!string.IsNullOrEmpty(state) && !StateRx.IsMatch(state))
            return new BadRequestObjectResult(new { error = "State must be 2 letters (e.g. NC)." });
        if (zip   is { Length: > 10 })  return new BadRequestObjectResult(new { error = "Zip is too long (10 max)." });
        if (phone is { Length: > 30 })  return new BadRequestObjectResult(new { error = "Phone is too long (30 max)." });
        if (how   is { Length: > 200 }) return new BadRequestObjectResult(new { error = "\"How did you hear about us\" is too long (200 max)." });

        // 4. Register (proc lower-cases email, upper-cases state, dedupes by email).
        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(@"
EXEC dbo.sp_RegisterMember
  @first_name = @First, @last_name = @Last, @email = @Email, @phone = @Phone,
  @city = @City, @state = @State, @zip = @Zip, @how_heard = @How, @source = 'web'",
            new
            {
                First = first, Last = last, Email = email,
                Phone = string.IsNullOrEmpty(phone) ? null : phone,
                City  = string.IsNullOrEmpty(city)  ? null : city,
                State = string.IsNullOrEmpty(state) ? null : state,
                Zip   = string.IsNullOrEmpty(zip)   ? null : zip,
                How   = string.IsNullOrEmpty(how)   ? null : how
            });
        if (row == null) return new ObjectResult(new { error = "sp_RegisterMember returned no rows" }) { StatusCode = 500 };

        string memberNumber = ((string)row.member_number).Trim();
        bool already = (bool)row.already_registered;
        _log.LogInformation("RegisterMember: {Number} ({Status})", memberNumber, already ? "existing" : "new");

        // 5.
        return new OkObjectResult(new { memberNumber, alreadyRegistered = already });
    }

    private const string ListSql = @"
SELECT id, member_number, first_name, last_name, email, phone, city, state, zip, how_heard, source, created_at
FROM dbo.members ORDER BY created_at DESC";

    [Function("ListMembers")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "members")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(ListSql)).ToList();
        return new OkObjectResult(rows);
    }

    /// <summary>
    /// CSV of every member, same order as the list. RFC-4180 quoting, UTF-8 BOM
    /// so Excel opens it cleanly. Never includes anything a mail-merge doesn't need.
    /// </summary>
    [Function("ExportMembersCsv")]
    public async Task<IActionResult> ExportCsv(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "members/export.csv")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(ListSql)).ToList();

        var sb = new StringBuilder();
        sb.Append((char)0xFEFF);   // UTF-8 BOM — Excel needs it to read UTF-8 correctly
        sb.Append("member_number,first_name,last_name,email,phone,city,state,zip,how_heard,source,created_at\r\n");
        foreach (var r in rows)
        {
            var d = (IDictionary<string, object?>)r;
            var createdAt = d["created_at"] is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ssZ") : (d["created_at"]?.ToString() ?? "");
            sb.Append(string.Join(",", new[]
            {
                CsvField(d["member_number"]?.ToString()?.Trim()),
                CsvField(d["first_name"]?.ToString()),
                CsvField(d["last_name"]?.ToString()),
                CsvField(d["email"]?.ToString()),
                CsvField(d["phone"]?.ToString()),
                CsvField(d["city"]?.ToString()),
                CsvField(d["state"]?.ToString()),
                CsvField(d["zip"]?.ToString()),
                CsvField(d["how_heard"]?.ToString()),
                CsvField(d["source"]?.ToString()),
                CsvField(createdAt)
            }));
            sb.Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"nsl-members-{DateTime.UtcNow:yyyyMMdd}.csv";
        _log.LogInformation("ExportMembersCsv: {N} member(s) -> {File}", rows.Count, fileName);
        return new FileContentResult(bytes, "text/csv; charset=utf-8") { FileDownloadName = fileName };
    }

    /// <summary>RFC-4180: quote when the value has a comma, quote or newline; double embedded quotes.</summary>
    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

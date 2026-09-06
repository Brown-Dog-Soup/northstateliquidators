using Dapper;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NSL.Api.Services;

/// <summary>
/// Public-UPC-database fallback lookup, used by /api/lookup when the local
/// lpn_catalog misses on a non-Amazon UPC (and, on catalog hits, to show a
/// market price next to the manifest price).
///
/// Provider: UPCitemdb free tier — 100 calls/day, no API key. That cap is
/// shared by everyone scanning, so every call is precious:
///
///   * Every provider answer (hit OR miss) is written to dbo.upc_lookup_cache.
///     The same barcode never costs a second call. Misses are retried after
///     <see cref="MissRetry"/>; rate-limit and error rows after <see cref="ErrorRetry"/>.
///   * Every provider call is logged to dbo.upc_lookup_log so the scanner can
///     show "online lookups today: n/100" and say plainly when the cap is hit.
///
/// Swap to a paid provider (UPCitemdb DEV, Go-UPC) only if the stats endpoint
/// shows the cap actually being reached on real receiving days.
/// </summary>
public sealed class UpcLookupService
{
    public const int DailyCap = 100;
    private static readonly TimeSpan MissRetry  = TimeSpan.FromDays(7);
    private static readonly TimeSpan ErrorRetry = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly SqlService _sql;
    private readonly ILogger<UpcLookupService> _log;

    public UpcLookupService(IHttpClientFactory httpFactory, SqlService sql, ILogger<UpcLookupService> log)
    {
        _http = httpFactory.CreateClient(nameof(UpcLookupService));
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NSL-Inventory/1.0 (+https://northstateliquidators.com)");
        _sql = sql;
        _log = log;
    }

    public sealed record UpcResult
    {
        public string? Title { get; init; }
        public string? Brand { get; init; }
        public string? Description { get; init; }
        public string? Category { get; init; }
        public decimal? Msrp { get; init; }
        public string? Upc { get; init; }
        public string? Ean { get; init; }
        public string? Asin { get; init; }
        public string? ImageUrl { get; init; }
        public string Source { get; init; } = "upcitemdb";
    }

    /// <summary>What happened on this lookup, for the UI to explain itself.</summary>
    public enum MarketStatus
    {
        Skipped,      // not a UPC-shaped code, or check digit failed — provider not consulted
        Hit,          // provider answered with a product (fresh call)
        Miss,         // provider had nothing (fresh call)
        RateLimited,  // provider returned 429 — daily cap reached
        Error,        // provider unreachable / unexpected response
        CachedHit,    // served from dbo.upc_lookup_cache, no provider call
        CachedMiss    // provider said "unknown" recently; not re-asked
    }

    public sealed record UpcOutcome(MarketStatus Status, UpcResult? Result, int? HttpStatus)
    {
        public string StatusKey => Status switch
        {
            MarketStatus.Skipped     => "skipped",
            MarketStatus.Hit         => "hit",
            MarketStatus.Miss        => "miss",
            MarketStatus.RateLimited => "rate_limited",
            MarketStatus.Error       => "error",
            MarketStatus.CachedHit   => "cached_hit",
            MarketStatus.CachedMiss  => "cached_miss",
            _ => "unknown"
        };
        public bool FromCache => Status is MarketStatus.CachedHit or MarketStatus.CachedMiss;
    }

    public async Task<UpcOutcome> LookupAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return new(MarketStatus.Skipped, null, null);
        var trimmed = code.Trim();

        // Only attempt UPC/EAN-shaped codes (12 or 13 digits)
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{12,13}$"))
            return new(MarketStatus.Skipped, null, null);

        // Check-digit validation — saves an API call and gives us a clean signal
        // for "scanner misread / typo" vs "valid UPC, just unknown to the catalog".
        if (!IsValidGtinCheckDigit(trimmed))
        {
            _log.LogInformation("UPCitemdb {Code} -> skipped (invalid GTIN check digit)", trimmed);
            return new(MarketStatus.Skipped, null, null);
        }

        // 1. Cache first. Any failure here is logged and treated as a cache miss;
        //    the cache must never take the scanner down.
        try
        {
            await using var conn = await _sql.OpenAsync(ct);
            var cached = await conn.QueryFirstOrDefaultAsync("EXEC dbo.sp_UpcCacheGet @upc = @u", new { u = trimmed });
            if (cached != null)
            {
                string status = (string)cached.status;
                DateTime lookedUp = (DateTime)cached.looked_up_at;
                var age = DateTime.UtcNow - lookedUp;

                if (status == "hit")
                {
                    _log.LogInformation("UPC {Code} -> cache hit (age {Age:F1}h)", trimmed, age.TotalHours);
                    return new(MarketStatus.CachedHit, FromCacheRow(cached), null);
                }
                if (status == "miss" && age < MissRetry)
                {
                    _log.LogInformation("UPC {Code} -> cached miss (age {Age:F1}h), provider not called", trimmed, age.TotalHours);
                    return new(MarketStatus.CachedMiss, null, null);
                }
                if ((status == "rate_limited" || status == "error") && age < ErrorRetry)
                {
                    // Don't burn the (possibly recovering) provider on a code we
                    // just failed on. Report the original condition.
                    _log.LogInformation("UPC {Code} -> cached {Status} (age {Age:F0}m), provider not called", trimmed, status, age.TotalMinutes);
                    return new(status == "rate_limited" ? MarketStatus.RateLimited : MarketStatus.Error, null, null);
                }
                // stale miss / stale error → fall through and ask again
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UPC cache read failed for {Code}; continuing to provider", trimmed);
        }

        // 2. Provider.
        var outcome = await CallProviderAsync(trimmed, ct);

        // 3. Remember the answer, whatever it was.
        try
        {
            await using var conn = await _sql.OpenAsync(ct);
            var r = outcome.Result;
            await conn.ExecuteAsync("EXEC dbo.sp_UpcCachePut @upc, @status, @http_status, @source, @title, @brand, @description, @category, @market_price, @asin, @ean, @image_url",
                new
                {
                    upc = trimmed,
                    status = outcome.StatusKey,
                    http_status = outcome.HttpStatus,
                    source = r?.Source ?? "upcitemdb",
                    title = Trunc(r?.Title, 500),
                    brand = Trunc(r?.Brand, 200),
                    description = r?.Description,
                    category = Trunc(r?.Category, 200),
                    market_price = r?.Msrp,
                    asin = Trunc(r?.Asin, 20),
                    ean = Trunc(r?.Ean, 20),
                    image_url = Trunc(r?.ImageUrl, 1000),
                });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UPC cache write failed for {Code}", trimmed);
        }

        return outcome;
    }

    /// <summary>Today's provider usage and cache size, for the scanner badge and /api/lookup-stats.</summary>
    public async Task<object?> GetStatsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _sql.OpenAsync(ct);
            var row = await conn.QueryFirstOrDefaultAsync("EXEC dbo.sp_UpcLookupStats");
            if (row == null) return null;
            var d = (IDictionary<string, object>)row;
            d["cap"] = DailyCap;
            d["remaining"] = Math.Max(0, DailyCap - Convert.ToInt32(d["calls_today"] ?? 0));
            return row;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UPC stats query failed");
            return null;
        }
    }

    private async Task<UpcOutcome> CallProviderAsync(string upc, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.upcitemdb.com/prod/trial/lookup?upc={Uri.EscapeDataString(upc)}";
            using var resp = await _http.GetAsync(url, ct);
            int http = (int)resp.StatusCode;

            if (http == 429)
            {
                _log.LogWarning("UPCitemdb {Code} -> 429 rate limited (daily cap {Cap} reached)", upc, DailyCap);
                return new(MarketStatus.RateLimited, null, http);
            }
            if (http == 404)
            {
                _log.LogInformation("UPCitemdb {Code} -> 404 not found", upc);
                return new(MarketStatus.Miss, null, http);
            }
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogInformation("UPCitemdb {Code} -> HTTP {Status}", upc, http);
                return new(MarketStatus.Error, null, http);
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc  = JsonSerializer.Deserialize<UpcItemDbResponse>(json, JsonOpts);
            var item = doc?.Items?.FirstOrDefault();
            if (item == null || string.IsNullOrWhiteSpace(item.Title))
            {
                _log.LogInformation("UPCitemdb {Code} -> no items", upc);
                return new(MarketStatus.Miss, null, http);
            }

            var result = new UpcResult
            {
                Title       = item.Title,
                Brand       = string.IsNullOrWhiteSpace(item.Brand) ? null : item.Brand,
                Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
                Category    = string.IsNullOrWhiteSpace(item.Category) ? null : item.Category,
                // Prefer lowest recorded price (highest is often an outlier reseller
                // listing). UPCitemdb stores 0 when it has no real low, so treat
                // non-positive as "missing" and fall back to highest.
                Msrp        = (item.LowestRecordedPrice.HasValue && item.LowestRecordedPrice > 0m)
                                ? item.LowestRecordedPrice
                                : (item.HighestRecordedPrice > 0m ? item.HighestRecordedPrice : null),
                Upc         = item.Upc,
                Ean         = item.Ean,
                Asin        = string.IsNullOrWhiteSpace(item.Asin) ? null : item.Asin,
                ImageUrl    = item.Images?.FirstOrDefault()
            };
            _log.LogInformation("UPCitemdb {Code} -> hit ({Title})", upc, result.Title);
            return new(MarketStatus.Hit, result, http);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UPCitemdb lookup failed for {Code}", upc);
            return new(MarketStatus.Error, null, null);
        }
    }

    private static UpcResult FromCacheRow(dynamic row)
    {
        var d = (IDictionary<string, object>)row;
        object? G(string k) => d.TryGetValue(k, out var v) ? v : null;
        return new UpcResult
        {
            Title       = G("title") as string,
            Brand       = G("brand") as string,
            Description = G("description") as string,
            Category    = G("category") as string,
            Msrp        = G("market_price") as decimal?,
            Upc         = G("upc") as string,
            Ean         = G("ean") as string,
            Asin        = G("asin") as string,
            ImageUrl    = G("image_url") as string,
            Source      = (G("source") as string) ?? "upcitemdb",
        };
    }

    private static string? Trunc(string? s, int max) => s == null ? null : (s.Length <= max ? s : s[..max]);

    /// <summary>
    /// Validates UPC-A (12) / EAN-13 (13) check digit using the GTIN modulo-10 algorithm.
    /// Skips the network call when the barcode is structurally malformed (typo, scanner misread).
    /// </summary>
    public static bool IsValidGtinCheckDigit(string digits)
    {
        if (string.IsNullOrEmpty(digits) || (digits.Length != 12 && digits.Length != 13)) return false;
        int sum = 0;
        // Walk right-to-left excluding the check digit; alternate weights 3,1
        for (int i = digits.Length - 2, w = 3; i >= 0; i--, w = 4 - w)
        {
            if (digits[i] < '0' || digits[i] > '9') return false;
            sum += (digits[i] - '0') * w;
        }
        int check = (10 - sum % 10) % 10;
        return check == digits[^1] - '0';
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class UpcItemDbResponse
    {
        [JsonPropertyName("code")]   public string? Code { get; set; }
        [JsonPropertyName("items")]  public List<Item>? Items { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("upc")]                   public string? Upc { get; set; }
        [JsonPropertyName("ean")]                   public string? Ean { get; set; }
        [JsonPropertyName("asin")]                  public string? Asin { get; set; }
        [JsonPropertyName("title")]                 public string? Title { get; set; }
        [JsonPropertyName("brand")]                 public string? Brand { get; set; }
        [JsonPropertyName("description")]           public string? Description { get; set; }
        [JsonPropertyName("category")]              public string? Category { get; set; }
        [JsonPropertyName("lowest_recorded_price")] public decimal? LowestRecordedPrice { get; set; }
        [JsonPropertyName("highest_recorded_price")]public decimal? HighestRecordedPrice { get; set; }
        [JsonPropertyName("images")]                public List<string>? Images { get; set; }
    }
}

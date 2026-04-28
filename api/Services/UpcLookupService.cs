using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NSL.Api.Services;

/// <summary>
/// Public-UPC-database fallback lookup. Used by /api/lookup when the local
/// lpn_catalog misses on a non-Amazon UPC. Currently calls UPCitemdb's free
/// trial endpoint (100/day, no API key). Swap to a paid provider (Go-UPC,
/// UPCitemdb paid tier, Keepa) when production volume warrants.
/// </summary>
public sealed class UpcLookupService
{
    private readonly HttpClient _http;
    private readonly ILogger<UpcLookupService> _log;

    public UpcLookupService(IHttpClientFactory httpFactory, ILogger<UpcLookupService> log)
    {
        _http = httpFactory.CreateClient(nameof(UpcLookupService));
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NSL-Inventory/1.0 (+https://northstateliquidators.com)");
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
        public string? ImageUrl { get; init; }
        public string Source { get; init; } = "upcitemdb";
    }

    /// <summary>
    /// Returns null on miss / error / rate-limit; UpcResult on hit.
    /// </summary>
    public async Task<UpcResult?> LookupAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();

        // Only attempt UPC/EAN-shaped codes (12 or 13 digits)
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{12,13}$"))
            return null;

        // Check-digit validation — saves an API call and gives us a clean signal
        // for "scanner misread / typo" vs "valid UPC, just unknown to the catalog".
        if (!IsValidGtinCheckDigit(trimmed))
        {
            _log.LogInformation("UPCitemdb {Code} -> skipped (invalid GTIN check digit)", trimmed);
            return null;
        }

        try
        {
            var url = $"https://api.upcitemdb.com/prod/trial/lookup?upc={Uri.EscapeDataString(trimmed)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogInformation("UPCitemdb {Code} -> HTTP {Status}", trimmed, (int)resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc  = JsonSerializer.Deserialize<UpcItemDbResponse>(json, JsonOpts);
            var item = doc?.Items?.FirstOrDefault();
            if (item == null || string.IsNullOrWhiteSpace(item.Title)) return null;

            return new UpcResult
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
                                : item.HighestRecordedPrice,
                Upc         = item.Upc,
                Ean         = item.Ean,
                ImageUrl    = item.Images?.FirstOrDefault()
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UPCitemdb lookup failed for {Code}", trimmed);
            return null;
        }
    }

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
        [JsonPropertyName("title")]                 public string? Title { get; set; }
        [JsonPropertyName("brand")]                 public string? Brand { get; set; }
        [JsonPropertyName("description")]           public string? Description { get; set; }
        [JsonPropertyName("category")]              public string? Category { get; set; }
        [JsonPropertyName("lowest_recorded_price")] public decimal? LowestRecordedPrice { get; set; }
        [JsonPropertyName("highest_recorded_price")]public decimal? HighestRecordedPrice { get; set; }
        [JsonPropertyName("images")]                public List<string>? Images { get; set; }
    }
}

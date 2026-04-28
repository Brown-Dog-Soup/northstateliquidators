using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;

namespace NSL.Api.Functions;

/// <summary>
/// POST /api/upload-photo?kind=pallet|item&id={guid}
///
/// Accepts the raw image bytes as the request body. Writes to the
/// scan-photos blob container under either pallets/{id}.jpg or items/{id}.jpg.
/// Updates dbo.manifests.photo_url or dbo.line_items.photo_blob_url to the
/// returned URL. Returns the URL.
/// </summary>
public sealed class UploadPhotoFunction
{
    private readonly BlobService _blob;
    private readonly SqlService _sql;
    private readonly ILogger<UploadPhotoFunction> _log;

    public UploadPhotoFunction(BlobService blob, SqlService sql, ILogger<UploadPhotoFunction> log)
    {
        _blob = blob;
        _sql = sql;
        _log = log;
    }

    [Function("UploadPhoto")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "upload-photo")] HttpRequest req,
        CancellationToken ct)
    {
        var kind = (string?)req.Query["kind"] ?? "";
        var idStr = (string?)req.Query["id"] ?? "";
        if (kind != "pallet" && kind != "item")
            return new BadRequestObjectResult(new { error = "kind must be 'pallet' or 'item'" });
        if (!Guid.TryParse(idStr, out var id))
            return new BadRequestObjectResult(new { error = "id must be a valid GUID" });

        // Read body into memory (photos are <10 MB even at high quality)
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return new BadRequestObjectResult(new { error = "Empty body" });

        var contentType = req.ContentType ?? "image/jpeg";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new BadRequestObjectResult(new { error = "Content-Type must be image/*" });

        var ext = contentType.Replace("image/", "").Split(';')[0].Trim() switch
        {
            "jpeg" or "jpg" => "jpg",
            "png"           => "png",
            "webp"          => "webp",
            _               => "bin"
        };
        var path = $"{kind}s/{id}.{ext}";

        ms.Position = 0;
        var url = await _blob.UploadAsync("scan-photos", path, ms, contentType, ct);

        // Persist the URL on the right table
        await using var conn = await _sql.OpenAsync(ct);
        if (kind == "pallet")
        {
            await conn.ExecuteAsync(
                "UPDATE dbo.manifests SET photo_url = @url, updated_at = SYSUTCDATETIME() WHERE id = @id",
                new { url, id });
        }
        else // item
        {
            await conn.ExecuteAsync(
                "UPDATE dbo.line_items SET photo_blob_url = @url WHERE id = @id",
                new { url, id });
        }

        _log.LogInformation("Uploaded photo for {Kind} {Id}: {Url}", kind, id, url);
        return new OkObjectResult(new { url, kind, id });
    }
}

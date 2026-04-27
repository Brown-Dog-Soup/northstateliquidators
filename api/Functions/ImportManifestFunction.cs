using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Models;
using NSL.Api.Services;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// POST /api/import-manifest
///
/// Accepts an Amazon B-Stock manifest XLSX as the request body, parses it
/// into LpnCatalogEntry rows, upserts into dbo.lpn_catalog, and writes an
/// audit row into dbo.manifest_imports.
///
/// Idempotent on the file's SHA-256: the same XLSX uploaded twice returns
/// the prior import id without re-processing.
///
/// Headers expected:
///   x-filename: original filename of the XLSX (for audit + source_manifest)
///   x-imported-by: optional, defaults to "anonymous"
/// </summary>
public sealed class ImportManifestFunction
{
    private readonly ManifestParser _parser;
    private readonly SqlService _sql;
    private readonly ILogger<ImportManifestFunction> _log;

    public ImportManifestFunction(ManifestParser parser, SqlService sql, ILogger<ImportManifestFunction> log)
    {
        _parser = parser;
        _sql = sql;
        _log = log;
    }

    [Function("ImportManifest")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "import-manifest")] HttpRequest req,
        CancellationToken ct)
    {
        var filename = req.Headers["x-filename"].FirstOrDefault() ?? "manifest.xlsx";
        var importedBy = req.Headers["x-imported-by"].FirstOrDefault() ?? "anonymous";

        // Read full body into memory (manifests are <5 MB even at 10k+ rows)
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return new BadRequestObjectResult(new { error = "Empty body — POST the XLSX as the request body." });

        var sha256 = ManifestParser.ComputeSha256(bytes);
        _log.LogInformation("Import request: filename={F} bytes={B} sha256={H}", filename, bytes.Length, sha256);

        // Dedupe: same SHA → same import
        var prior = await _sql.FindImportBySha256Async(sha256, ct);
        if (prior.HasValue)
        {
            _log.LogInformation("Duplicate import (sha already seen): prior id {Prior}", prior.Value);
            return new OkObjectResult(new ManifestImportResult
            {
                ImportId = prior.Value,
                Filename = filename,
                Sha256 = sha256,
                DuplicateOfPriorImport = true,
                PriorImportId = prior.Value.ToString()
            });
        }

        // Parse the XLSX
        ManifestParser.ParseResult parseResult;
        try
        {
            ms.Position = 0;
            parseResult = _parser.Parse(ms, filename);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manifest parse failed for {F}", filename);
            return new BadRequestObjectResult(new { error = "Parse failed", detail = ex.Message });
        }

        if (parseResult.Entries.Count == 0)
            return new BadRequestObjectResult(new { error = "No LPN entries found in manifest." });

        // Upsert
        var (inserted, updated) = await _sql.UpsertLpnCatalogAsync(parseResult.Entries, ct);

        // Audit row
        var importId = await _sql.InsertManifestImportAsync(
            filename: filename,
            sha256: sha256,
            palletReference: parseResult.PalletReference,
            orderNumber: parseResult.OrderNumber,
            rowCount: parseResult.Entries.Count,
            rowsInserted: inserted,
            rowsUpdated: updated,
            rowsSkipped: 0,
            unmappedColumnsJson: JsonSerializer.Serialize(parseResult.UnmappedColumns),
            importedBy: importedBy,
            archiveBlobUrl: null,
            ct: ct);

        return new OkObjectResult(new ManifestImportResult
        {
            ImportId = importId,
            Filename = filename,
            Sha256 = sha256,
            RowCount = parseResult.Entries.Count,
            RowsInserted = inserted,
            RowsUpdated = updated,
            RowsSkipped = 0,
            UnmappedColumns = parseResult.UnmappedColumns,
            DuplicateOfPriorImport = false
        });
    }
}

using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NSL.Api.Services;

/// <summary>
/// Wraps Azure Blob Storage with shared-key auth (since SWA managed Functions
/// don't expose IMDS for managed-identity auth). Account name + key come from
/// SWA app settings.
/// </summary>
public sealed class BlobService
{
    private readonly BlobServiceClient _client;
    private readonly ILogger<BlobService> _log;

    public BlobService(IConfiguration config, ILogger<BlobService> log)
    {
        var account = config["StorageAccountName"]
            ?? throw new InvalidOperationException("StorageAccountName app setting missing.");
        var key = config["StorageAccountKey"]
            ?? throw new InvalidOperationException("StorageAccountKey app setting missing.");
        var credential = new StorageSharedKeyCredential(account, key);
        var uri = new Uri($"https://{account}.blob.core.windows.net");
        _client = new BlobServiceClient(uri, credential);
        _log = log;
    }

    /// <summary>
    /// Uploads a binary stream to a blob and returns its public URL. Container
    /// must already exist (provisioned by Bicep). Path is the relative path
    /// inside the container (e.g., "pallets/abc.jpg").
    /// </summary>
    public async Task<string> UploadAsync(
        string container, string path, Stream content, string contentType, CancellationToken ct = default)
    {
        var c = _client.GetBlobContainerClient(container);
        var blob = c.GetBlobClient(path);
        await blob.UploadAsync(content, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        _log.LogInformation("Uploaded {Container}/{Path} ({Bytes} bytes)", container, path, content.Length);
        return blob.Uri.ToString();
    }

    /// <summary>
    /// Generates a short-lived read-only SAS URL for a blob so we can render
    /// it in the browser without making the whole container public.
    /// </summary>
    public string GenerateReadSas(string container, string path, TimeSpan validity)
    {
        var c = _client.GetBlobContainerClient(container);
        var blob = c.GetBlobClient(path);
        var sasUri = blob.GenerateSasUri(
            Azure.Storage.Sas.BlobSasPermissions.Read,
            DateTimeOffset.UtcNow.Add(validity));
        return sasUri.ToString();
    }
}

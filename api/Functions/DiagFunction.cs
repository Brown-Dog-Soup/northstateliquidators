using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace NSL.Api.Functions;

/// <summary>
/// GET /api/diag — dumps managed-identity env vars + the platform context so
/// we can debug why DefaultAzureCredential can't reach IMDS in SWA managed
/// Functions. Will be removed after we figure out auth.
/// </summary>
public sealed class DiagFunction
{
    [Function("Diag")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diag")] HttpRequest req)
    {
        // Standard App Service / Functions identity endpoint env vars
        var identityKeys = new[] {
            "IDENTITY_ENDPOINT", "IDENTITY_HEADER",
            "MSI_ENDPOINT", "MSI_SECRET",
            "AZURE_CLIENT_ID", "AZURE_TENANT_ID",
            "WEBSITE_AUTH_ENABLED",
            "FUNCTIONS_WORKER_RUNTIME", "FUNCTIONS_EXTENSION_VERSION",
            "WEBSITE_SITE_NAME", "WEBSITE_RESOURCE_GROUP",
            "WEBSITE_OWNER_NAME"
        };

        var dump = identityKeys.ToDictionary(
            k => k,
            k => Environment.GetEnvironmentVariable(k) ?? "<not set>");

        // Attempt to call IMDS directly to confirm reachability
        string? imdsResponse = null;
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")
                ?? "http://169.254.169.254/metadata/identity/oauth2/token";
            var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
            var url = $"{endpoint}?api-version=2019-08-01&resource=https://database.windows.net/";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(header))
                msg.Headers.Add("X-IDENTITY-HEADER", header);
            else
                msg.Headers.Add("Metadata", "true");
            var r = http.SendAsync(msg).GetAwaiter().GetResult();
            var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            imdsResponse = $"{(int)r.StatusCode} {r.StatusCode}: {body[..Math.Min(body.Length, 400)]}";
        }
        catch (Exception ex)
        {
            imdsResponse = $"exception: {ex.Message}";
        }

        return new OkObjectResult(new
        {
            timestamp = DateTimeOffset.UtcNow,
            envVars = dump,
            imdsProbe = imdsResponse,
            sqlConnString = (Environment.GetEnvironmentVariable("SqlConnectionString") ?? "<not set>").Length
        });
    }
}

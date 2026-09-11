using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NSL.Api.Services;

/// <summary>
/// Who is calling a staff route. SWA injects the signed-in user as the
/// x-ms-client-principal header: base64 JSON
/// {identityProvider, userId, userDetails, userRoles}. userDetails is the
/// preferred_username claim (staticwebapp.config.json), i.e. the email.
/// Used only for the audit trail (manifest_history.changed_by).
/// </summary>
public static class ClientPrincipal
{
    /// <summary>userDetails from the SWA client-principal header; null when absent or unparsable.</summary>
    public static string? UserDetails(HttpRequest req)
    {
        var header = req.Headers["x-ms-client-principal"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(header));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var details = doc.RootElement.TryGetProperty("userDetails", out var ud) ? ud.GetString() : null;
            return string.IsNullOrWhiteSpace(details) ? null : details;
        }
        catch (Exception)
        {
            return null;   // malformed header — never fail a request over the audit column
        }
    }
}

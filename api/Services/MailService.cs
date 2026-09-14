using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NSL.Api.Services;

/// <summary>
/// Transactional mail via Microsoft Graph sendMail, sent AS the hello@ shared
/// mailbox on the TenantIQ Pro tenant. Send-only; replies reach hello@ by MX.
///
/// Config (SWA application settings):
///   MAIL_ENABLED        "true" to actually send (kill switch; default OFF)
///   MAIL_FROM           hello@northstateliquidators.com — the sending mailbox
///   MAIL_TENANT_ID      d9b645c3-3587-4cd4-be9b-1a8d405c92ad
///   MAIL_CLIENT_ID      NSL-Website-Mail app registration (client id)
///   MAIL_CLIENT_SECRET  its client secret
///   MAIL_REPLY_TO       optional; defaults to MAIL_FROM
///   SITE_BASE_URL       optional; defaults to https://northstateliquidators.com
///
/// Credential: ClientSecretCredential. Managed identity is NOT usable from a
/// SWA-MANAGED Functions API (no IMDS — same reason SqlService and BlobService
/// use connection strings / shared keys). If the API ever moves to "bring your
/// own Functions", drop MAIL_CLIENT_ID/SECRET and this falls through to
/// ManagedIdentityCredential with no other change.
///
/// The app's Mail.Send is confined to MAIL_FROM in Exchange Online (RBAC for
/// Applications — see the design runbook). A 403 ErrorAccessDenied here means
/// the scoping is wrong or has not propagated, NOT the code.
/// </summary>
public sealed class MailService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MailService> _log;
    private readonly TokenCredential _credential;
    private static readonly string[] Scopes = { "https://graph.microsoft.com/.default" };

    public bool   Enabled { get; }
    public string From    { get; }
    private readonly string _replyTo;
    private readonly string _siteBase;

    public MailService(IHttpClientFactory http, IConfiguration cfg, ILogger<MailService> log)
    {
        _http = http;
        _log = log;
        Enabled   = string.Equals(cfg["MAIL_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
        From      = cfg["MAIL_FROM"] ?? "";
        _replyTo  = cfg["MAIL_REPLY_TO"] ?? From;
        _siteBase = (cfg["SITE_BASE_URL"] ?? "https://northstateliquidators.com").TrimEnd('/');

        var tenant = cfg["MAIL_TENANT_ID"];
        var client = cfg["MAIL_CLIENT_ID"];
        var secret = cfg["MAIL_CLIENT_SECRET"];
        _credential = !string.IsNullOrEmpty(tenant) && !string.IsNullOrEmpty(client) && !string.IsNullOrEmpty(secret)
            ? new ClientSecretCredential(tenant, client, secret)
            : new ManagedIdentityCredential();   // only reachable on a BYO-Functions backend
    }

    public bool Configured => Enabled && From.Length > 0;

    public sealed record MailResult(bool Sent, string? Error, int? StatusCode);

    /// <summary>Graph sendMail body. saveToSentItems is deliberately omitted (default true → copy in hello@ Sent Items).</summary>
    public static object BuildSendMailPayload(string toAddress, string toName, string subject, string html, string replyTo) => new
    {
        message = new
        {
            subject,
            body = new { contentType = "HTML", content = html },
            toRecipients = new[] { new { emailAddress = new { address = toAddress, name = toName } } },
            replyTo = new[] { new { emailAddress = new { address = replyTo } } },
        }
    };

    public async Task<MailResult> SendAsync(string toAddress, string toName, string subject, string html, CancellationToken ct)
    {
        if (!Configured) return new MailResult(false, "mail not configured/enabled", null);

        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);

        var c = _http.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(8);   // SWA caps a whole API request at 45 s
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var json = JsonSerializer.Serialize(BuildSendMailPayload(toAddress, toName, subject, html, _replyTo));
        var resp = await c.PostAsync(
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(From)}/sendMail",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);

        if (resp.StatusCode == HttpStatusCode.Accepted) return new MailResult(true, null, 202);

        var body = await resp.Content.ReadAsStringAsync(ct);
        var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalSeconds;
        var err = (body.Length > 500 ? body[..500] : body) + (retryAfter is null ? "" : $" (retry-after {retryAfter}s)");
        return new MailResult(false, err, (int)resp.StatusCode);
    }

    /// <summary>
    /// Best effort. NEVER throws — a mail problem must not turn a successful
    /// signup into an error the visitor sees. Returns whether Graph accepted
    /// the message (202 = handed off, not delivered).
    /// </summary>
    public async Task<bool> SendMemberWelcomeAsync(MemberMail m, CancellationToken ct)
    {
        if (!Configured)
        {
            _log.LogInformation("MailService disabled — no welcome sent for member {Num}", m.MemberNumber);
            return false;
        }
        var (subject, html) = MemberEmailTemplates.Welcome(m, _siteBase);

        // One retry, transient classes only (429 / 5xx / exception). Runs on the
        // request path, so it is capped hard rather than backed off politely.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var r = await SendAsync(m.Email, m.FirstName, subject, html, ct);
                if (r.Sent)
                {
                    _log.LogInformation("Welcome mail accepted by Graph for member {Num}", m.MemberNumber);
                    return true;
                }
                var transient = r.StatusCode is 429 or >= 500;
                _log.Log(transient ? LogLevel.Warning : LogLevel.Error,
                    "Welcome mail failed for member {Num}: HTTP {Status} {Error}", m.MemberNumber, r.StatusCode, r.Error);
                if (!transient) return false;   // 401/403/404 will not fix themselves
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Welcome mail attempt {N} threw for member {Num}", attempt, m.MemberNumber);
            }
            if (attempt == 1)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return false; }
            }
        }
        return false;
    }
}

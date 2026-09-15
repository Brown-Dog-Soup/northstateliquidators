using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("api.Tests")]

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
///                       captured once at startup (singleton) — after rotating
///                       the secret, re-set the SWA setting; SWA restarts the
///                       API on a settings change.
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
        : this(http, cfg, log, BuildCredential(cfg))
    {
    }

    /// <summary>Credential seam for tests — bypasses BuildCredential's ClientSecretCredential/ManagedIdentityCredential selection.</summary>
    public MailService(IHttpClientFactory http, IConfiguration cfg, ILogger<MailService> log, TokenCredential credential)
    {
        _http = http;
        _log = log;
        Enabled   = string.Equals(cfg["MAIL_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
        From      = cfg["MAIL_FROM"] ?? "";
        _replyTo  = cfg["MAIL_REPLY_TO"] ?? From;
        _siteBase = (cfg["SITE_BASE_URL"] ?? "https://northstateliquidators.com").TrimEnd('/');
        _credential = credential;
    }

    private static TokenCredential BuildCredential(IConfiguration cfg)
    {
        var tenant = cfg["MAIL_TENANT_ID"];
        var client = cfg["MAIL_CLIENT_ID"];
        var secret = cfg["MAIL_CLIENT_SECRET"];
        return !string.IsNullOrEmpty(tenant) && !string.IsNullOrEmpty(client) && !string.IsNullOrEmpty(secret)
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
        var err = RedactError(body, toAddress, resp.ReasonPhrase ?? resp.StatusCode.ToString())
                  + (retryAfter is null ? "" : $" (retry-after {retryAfter}s)");
        return new MailResult(false, err, (int)resp.StatusCode);
    }

    /// <summary>
    /// Reduces a Graph error response to "{code}: {message}" (falling back to
    /// <paramref name="statusFallback"/> when the body isn't the expected JSON
    /// shape), then redacts any occurrence of the recipient address before the
    /// text is logged — the recipient's email must never land in logs via the
    /// raw Graph error body. Capped at 300 chars.
    /// </summary>
    internal static string RedactError(string body, string toAddress, string? statusFallback = null)
    {
        string message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                message = string.IsNullOrEmpty(code) && string.IsNullOrEmpty(msg)
                    ? statusFallback ?? body
                    : $"{code}: {msg}";
            }
            else
            {
                message = statusFallback ?? body;
            }
        }
        catch (JsonException)
        {
            message = statusFallback ?? body;
        }

        if (!string.IsNullOrEmpty(toAddress) && !string.IsNullOrEmpty(message))
        {
            message = message.Replace(toAddress, "[recipient]", StringComparison.OrdinalIgnoreCase);
        }

        return message.Length > 300 ? message[..300] : message;
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

        // The caller's ct is deliberately CancellationToken.None at both call
        // sites (a mail problem must never abort the signup POST or the staff
        // resend click). This 10 s budget — not the caller's ct — is what
        // actually bounds how long the attempt loop can run.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // One retry, transient classes only (429 / 5xx / exception). Runs on the
        // request path, so it is capped hard rather than backed off politely.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var r = await SendAsync(m.Email, m.FirstName, subject, html, budget.Token);
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
            catch (Azure.Identity.AuthenticationFailedException ex)
            {
                _log.LogError(ex, "Welcome mail: token acquisition failed for member {Num} — check MAIL_CLIENT_* / secret expiry", m.MemberNumber);
                return false;   // auth failures never retry
            }
            catch (Exception ex)
            {
                // Catches OperationCanceledException too: the unconditional
                // "never throws" contract on this method outranks propagating
                // a real cancellation — callers must always get a bool back.
                _log.LogWarning(ex, "Welcome mail attempt {N} threw for member {Num}", attempt, m.MemberNumber);
            }
            if (attempt == 1)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), budget.Token); } catch (Exception) { return false; }
            }
        }
        return false;
    }
}

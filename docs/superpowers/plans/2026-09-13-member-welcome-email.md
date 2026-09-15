# Member Welcome Email Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a new member signs up on the public site, email them their member number and welcome copy from hello@northstateliquidators.com, with a staff "Resend" button and an audit column; nothing about the signup itself may ever fail because of mail.

**Architecture:** A pure static `MemberEmailTemplates` renders subject/HTML/text; `MailService` (mirrors `SquareService`: HttpClient + config + kill switch) sends via Microsoft Graph `POST /users/{from}/sendMail` with a `ClientSecretCredential` from `Azure.Identity`. `RegisterMember` builds its response first, then best-effort sends and stamps `members.welcome_sent_at` through a proc. A staff route resends the welcome. Exchange Online RBAC for Applications confines the app registration to hello@ (runbook, Jeff's interactive session).

**Tech Stack:** .NET 8 isolated Azure Functions, Dapper, `Azure.Identity` 1.13.1 (already referenced), System.Text.Json, xUnit (`api.Tests`), T-SQL, `az` CLI, Exchange Online PowerShell 3.7.1.

**Spec:** `docs/superpowers/specs/2026-09-13-member-welcome-email-design.md` (APPROVED 2026-09-13; decisions in its Status line: welcome only, RBAC 5A, one shared mail identity, secret in app settings).

## Global Constraints

- Branch `feature/member-welcome-email`, created from `feature/square-hotfix` @ `4e1bc38` (that branch adds `api.Tests` and the CI test job; PR #14). PR targets `main` after #14 merges.
- v1 sends the **welcome only**. `alreadyRegistered` signups send nothing and the API response is unchanged (`memberNumber: null, alreadyRegistered: true`). No reminder template ships.
- The signup response is built BEFORE any mail work; the whole mail path is inside `try/catch`; mail uses `CancellationToken.None`; a failed stamp never undoes a sent mail.
- Config names (SWA app settings): `MAIL_ENABLED` (default off), `MAIL_FROM`, `MAIL_TENANT_ID`, `MAIL_CLIENT_ID`, `MAIL_CLIENT_SECRET`, optional `MAIL_REPLY_TO`, optional `SITE_BASE_URL` (default `https://northstateliquidators.com`). Tenant `d9b645c3-3587-4cd4-be9b-1a8d405c92ad`, from `hello@northstateliquidators.com`.
- Graph payload: `message.body.contentType = "HTML"`, `toRecipients` and `replyTo` are JSON ARRAYS, `saveToSentItems` omitted (default true → copy in hello@ Sent Items). HttpClient timeout 8 s; one retry after 2 s on 429/5xx/exception only.
- Logs carry the member number, never the recipient address.
- Templates: inline styles only, no images, no `<style>`, ≤560px; first name and number HTML-escaped; copy per spec §2(a) verbatim (phone `(919) 526-0112`, `hello@northstateliquidators.com`, Wake Forest NC, CTA to `{site}/shop.html?view=new`).
- `dotnet build api/api.csproj` 0 errors; `dotnet test api.Tests/api.Tests.csproj` all green (16 existing + new).
- DB migration `db/member-welcome-mail.sql` is idempotent and hand-applied to prod (`sql-nsl-prod-nc5h2y.database.windows.net` / `sqldb-nsl-prod`, Entra token). It re-creates `sp_RegisterMember` with widened result sets and grants `EXECUTE` on the new proc to `nsl_api`.
- Never grant the Entra `Mail.Send` consent (RBAC 5A union rule). Never reuse the "NSL Staff Portal" app or any other existing app registration.
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01SBzv2wxVMJKqKEsddmWY2Q`.

---

### Task 1: `MemberEmailTemplates` (pure) + tests

**Files:**
- Create: `api/Services/MemberEmailTemplates.cs`
- Create: `api.Tests/MemberEmailTemplatesTests.cs`

**Interfaces:**
- Produces: `namespace NSL.Api.Services`; `record MemberMail(string MemberNumber, string FirstName, string Email)`; `static class MemberEmailTemplates` with `static (string Subject, string Html) Welcome(MemberMail m, string siteBase)` and `static string WelcomeText(MemberMail m, string siteBase)`.

- [ ] **Step 1: Write the failing tests**

`api.Tests/MemberEmailTemplatesTests.cs`:
```csharp
using NSL.Api.Services;
using Xunit;

public class MemberEmailTemplatesTests
{
    private const string Site = "https://northstateliquidators.com";
    private static readonly MemberMail Sam = new("2600001", "Sam", "sam@example.com");

    [Fact]
    public void Welcome_RendersNumberAndName()
    {
        var (subject, html) = MemberEmailTemplates.Welcome(Sam, Site);
        Assert.Equal("You're member #2600001 — welcome to North State Liquidators", subject);
        Assert.Contains("2600001", html);
        Assert.Contains("Hi Sam", html);
        Assert.Contains("https://northstateliquidators.com/shop.html?view=new", html);
        Assert.Contains("(919) 526-0112", html);
    }

    [Fact]
    public void Welcome_EscapesHostileName()
    {
        var hostile = new MemberMail("2600002", "<script>alert(1)</script>", "x@example.com");
        var (subject, html) = MemberEmailTemplates.Welcome(hostile, Site);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", subject);
    }

    [Fact]
    public void Welcome_LeavesNoUnreplacedTokens()
    {
        var (subject, html) = MemberEmailTemplates.Welcome(Sam, Site);
        Assert.DoesNotContain("{{", subject);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("{{", MemberEmailTemplates.WelcomeText(Sam, Site));
    }

    [Fact]
    public void Welcome_HasNoStyleBlockOrImages()
    {
        var (_, html) = MemberEmailTemplates.Welcome(Sam, Site);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void WelcomeText_ContainsNumberAndPhone()
    {
        var text = MemberEmailTemplates.WelcomeText(Sam, Site);
        Assert.Contains("YOUR MEMBER NUMBER: 2600001", text);
        Assert.Contains("(919) 526-0112", text);
        Assert.Contains("Hi Sam", text);
    }

    [Fact]
    public void Welcome_TrimsTrailingSlashOnSiteBase()
    {
        var (_, html) = MemberEmailTemplates.Welcome(Sam, Site + "/");
        Assert.Contains("https://northstateliquidators.com/shop.html?view=new", html);
        Assert.DoesNotContain("com//shop", html);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: build FAILS — `MemberEmailTemplates` / `MemberMail` not found.

- [ ] **Step 3: Write the implementation**

`api/Services/MemberEmailTemplates.cs`:
```csharp
using System.Net;

namespace NSL.Api.Services;

/// <summary>What the welcome email needs to know about a member.</summary>
public sealed record MemberMail(string MemberNumber, string FirstName, string Email);

/// <summary>
/// Pure renderers for the member welcome email (spec §2(a)). No I/O, no
/// config — unit-tested directly. Inline styles only, no images, no
/// &lt;style&gt; block (Outlook), single column ≤560px. First name and number
/// are HTML-escaped: the name is free text typed by an anonymous visitor.
/// </summary>
public static class MemberEmailTemplates
{
    public const string Phone = "(919) 526-0112";
    public const string PhoneHref = "tel:+19195260112";
    public const string HelloAddress = "hello@northstateliquidators.com";

    public static (string Subject, string Html) Welcome(MemberMail m, string siteBase)
    {
        var site = siteBase.TrimEnd('/');
        var num = WebUtility.HtmlEncode(m.MemberNumber);
        var first = WebUtility.HtmlEncode(m.FirstName);
        var subject = $"You're member #{m.MemberNumber.Replace("<", "").Replace(">", "")} — welcome to North State Liquidators";
        var html = $@"<div style=""margin:0;padding:24px 12px;background:#f6f4ef;font-family:Segoe UI,Arial,Helvetica,sans-serif;color:#1d2330;"">
  <div style=""display:none;max-height:0;overflow:hidden;opacity:0;"">Your member number is {num}. Give it at the warehouse — save this email.</div>

  <div style=""max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e4dfd3;border-radius:10px;overflow:hidden;"">

    <div style=""background:#002868;padding:18px 24px;"">
      <a href=""{site}/"" style=""text-decoration:none;color:#ffffff;font-size:18px;font-weight:700;letter-spacing:0.04em;"">
        NORTH <span style=""color:#f2c14e;"">&#9733;</span> STATE LIQUIDATORS
      </a>
      <div style=""color:#c9cfe0;font-size:12px;margin-top:4px;"">Wake Forest, NC &middot; Family-owned</div>
    </div>

    <div style=""padding:24px;"">
      <p style=""margin:0 0 14px;font-size:16px;"">Hi {first} — you're in.</p>

      <div style=""margin:0 0 18px;padding:16px;background:#fff8e6;border:2px solid #f2c14e;border-radius:8px;text-align:center;"">
        <div style=""font-size:11px;letter-spacing:0.14em;text-transform:uppercase;color:#6b6350;"">Your member number</div>
        <div style=""font-size:34px;font-weight:700;letter-spacing:0.06em;font-family:Consolas,Menlo,monospace;color:#002868;margin-top:4px;"">{num}</div>
      </div>

      <p style=""margin:0 0 12px;font-size:15px;line-height:1.55;"">
        <strong>What to do with it:</strong> give that number at the register when you come to
        the warehouse, or say it on the phone. That's it — it's how we know you, and it's how
        you get member pricing.
      </p>

      <p style=""margin:0 0 12px;font-size:15px;line-height:1.55;"">
        <strong>What happens next:</strong> when new boxes and pallets hit the floor, we email
        members first — usually once or twice a week, never daily. Photos and the full manifest
        are on every box on the site.
      </p>

      <p style=""margin:0 0 12px;font-size:15px;line-height:1.55;"">
        Pickup is in Wake Forest. We deliver free to the Raleigh Flea Market every Friday,
        $10 within 20 miles of the warehouse, and we ship too.
      </p>

      <p style=""margin:22px 0;text-align:center;"">
        <a href=""{site}/shop.html?view=new""
           style=""display:inline-block;background:#bf0a30;color:#ffffff;text-decoration:none;padding:13px 26px;border-radius:6px;font-weight:700;font-size:15px;"">
          See what's on the floor &rarr;
        </a>
      </p>

      <p style=""margin:0 0 6px;font-size:15px;line-height:1.55;"">Questions? Call us — we answer the phone.</p>
      <p style=""margin:0;font-size:15px;line-height:1.55;"">
        <a href=""{PhoneHref}"" style=""color:#002868;font-weight:700;text-decoration:none;"">{Phone}</a>
        &nbsp;&middot;&nbsp;
        <a href=""mailto:{HelloAddress}"" style=""color:#002868;text-decoration:none;"">{HelloAddress}</a>
      </p>

      <p style=""margin:18px 0 0;font-size:15px;"">&mdash; Norm &amp; Rob</p>
    </div>

    <div style=""padding:14px 24px;background:#f2efe8;border-top:1px solid #e4dfd3;font-size:12px;color:#6b6350;line-height:1.5;"">
      North State Liquidators &middot; Warehouse in Wake Forest, NC (address by appointment)<br>
      You're getting this because you signed up for a member number at northstateliquidators.com.
      Reply to this email and a human will read it.
    </div>
  </div>
</div>";
        return (subject, html);
    }

    /// <summary>Plain-text twin (not sent in v1 — Graph body is HTML or Text, not both; kept for a future multipart upgrade and as the cheapest test surface).</summary>
    public static string WelcomeText(MemberMail m, string siteBase)
    {
        var site = siteBase.TrimEnd('/');
        return $@"Hi {m.FirstName} — you're in.

YOUR MEMBER NUMBER: {m.MemberNumber}

Give that number at the register when you come to the warehouse, or say it
on the phone. That's how we know you and how you get member pricing.

When new boxes and pallets hit the floor, we email members first — usually
once or twice a week, never daily. Photos and the full manifest are on every
box: {site}/shop.html?view=new

Pickup is in Wake Forest. Free delivery to the Raleigh Flea Market every
Friday, $10 within 20 miles, and we ship too.

Questions? Call us — we answer the phone. {Phone}
{HelloAddress}

— Norm & Rob
North State Liquidators · Wake Forest, NC";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: 22 passing (16 + 6).

- [ ] **Step 5: Commit**

```powershell
git add api/Services/MemberEmailTemplates.cs api.Tests/MemberEmailTemplatesTests.cs
git commit -m "feat(mail): member welcome email templates (pure, tested)"
```

---

### Task 2: `MailService` + Graph payload builder + tests + DI

**Files:**
- Create: `api/Services/MailService.cs`
- Create: `api.Tests/MailServiceTests.cs`
- Modify: `api/Program.cs` (register)

**Interfaces:**
- Consumes: `MemberEmailTemplates.Welcome`, `MemberMail` (Task 1).
- Produces: `sealed class MailService` with `bool Enabled`, `string From`, `bool Configured`, `record MailResult(bool Sent, string? Error, int? StatusCode)`, `static object BuildSendMailPayload(string toAddress, string toName, string subject, string html, string replyTo)`, `Task<MailResult> SendAsync(string toAddress, string toName, string subject, string html, CancellationToken ct)`, `Task<bool> SendMemberWelcomeAsync(MemberMail m, CancellationToken ct)` (never throws).

- [ ] **Step 1: Write the failing tests**

`api.Tests/MailServiceTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

public class MailServiceTests
{
    private sealed class NoHttp : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) { Calls++; throw new InvalidOperationException("HTTP must not be used"); }
    }

    private static MailService Build(params (string, string?)[] settings)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return new MailService(new NoHttp(), cfg, NullLogger<MailService>.Instance);
    }

    [Fact]
    public void Payload_MatchesGraphSendMailShape()
    {
        var payload = MailService.BuildSendMailPayload("to@example.com", "Sam", "Subj", "<p>hi</p>", "hello@northstateliquidators.com");
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("\"toRecipients\":[", json);   // an ARRAY, never a collapsed object
        Assert.Contains("\"replyTo\":[", json);
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("message");
        Assert.Equal("Subj", msg.GetProperty("subject").GetString());
        Assert.Equal("HTML", msg.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("<p>hi</p>", msg.GetProperty("body").GetProperty("content").GetString());
        var to = msg.GetProperty("toRecipients")[0].GetProperty("emailAddress");
        Assert.Equal("to@example.com", to.GetProperty("address").GetString());
        Assert.Equal("Sam", to.GetProperty("name").GetString());
        Assert.Equal("hello@northstateliquidators.com", msg.GetProperty("replyTo")[0].GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public void Payload_OmitsSaveToSentItems()
    {
        var json = JsonSerializer.Serialize(MailService.BuildSendMailPayload("a@b.c", "A", "s", "h", "r@b.c"));
        Assert.DoesNotContain("saveToSentItems", json);
    }

    [Fact]
    public void DisabledByDefault_NoHttp()
    {
        var svc = Build(("MAIL_FROM", "hello@northstateliquidators.com"));
        Assert.False(svc.Enabled);
        Assert.False(svc.Configured);
    }

    [Fact]
    public async Task SendMemberWelcome_WhenDisabled_ReturnsFalseWithoutHttp()
    {
        var http = new NoHttp();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["MAIL_ENABLED"] = "false", ["MAIL_FROM"] = "hello@northstateliquidators.com" }).Build();
        var svc = new MailService(http, cfg, NullLogger<MailService>.Instance);
        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);
        Assert.False(sent);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Configured_RequiresEnabledAndFrom()
    {
        Assert.True(Build(("MAIL_ENABLED", "true"), ("MAIL_FROM", "hello@northstateliquidators.com")).Configured);
        Assert.False(Build(("MAIL_ENABLED", "true")).Configured);
    }

    [Fact]
    public async Task SendMemberWelcome_NeverThrows_WhenHttpFails()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MAIL_ENABLED"] = "true", ["MAIL_FROM"] = "hello@northstateliquidators.com",
            ["MAIL_TENANT_ID"] = "00000000-0000-0000-0000-000000000000",
            ["MAIL_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001", ["MAIL_CLIENT_SECRET"] = "x"
        }).Build();
        var svc = new MailService(new NoHttp(), cfg, NullLogger<MailService>.Instance);
        // Token acquisition or HTTP will fail (bogus tenant / throwing factory); must return false, not throw.
        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);
        Assert.False(sent);
    }
}
```
Add to `api.Tests/api.Tests.csproj` `<ItemGroup>` of packages: `<PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.0" />` — only if the build says the in-memory configuration types are missing (they usually come transitively through the api project reference; `Microsoft.Extensions.Logging.Abstractions` does too).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: build FAILS — `MailService` not found.

- [ ] **Step 3: Write `MailService.cs`**

```csharp
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
```

- [ ] **Step 4: Register in DI**

`api/Program.cs`, after `builder.Services.AddSingleton<SquareService>();`:
```csharp
builder.Services.AddSingleton<MailService>();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: 28 passing. (`SendMemberWelcome_NeverThrows_WhenHttpFails` may take a few seconds while the bogus credential fails; it must still return false.)

- [ ] **Step 6: Commit**

```powershell
git add api/Services/MailService.cs api.Tests/MailServiceTests.cs api/Program.cs
git commit -m "feat(mail): MailService — Graph sendMail as hello@ with kill switch and bounded retry"
```

---

### Task 3: DB migration + list/CSV columns

**Files:**
- Create: `db/member-welcome-mail.sql`
- Modify: `api/Functions/MembersFunction.cs` — `ListSql`, `ExportCsv` header + fields

**Interfaces:**
- Produces: `dbo.members.welcome_sent_at DATETIME2 NULL`; `dbo.sp_StampMemberWelcomeSent @member_number CHAR(7)`; `sp_RegisterMember` now returns `member_number, already_registered, created_at, welcome_sent_at`; `GET /api/members` rows and the CSV gain `welcome_sent_at`.

- [ ] **Step 1: Write the migration**

```sql
-- ============================================================================
-- Member welcome mail (docs/superpowers/specs/2026-09-13-member-welcome-email-design.md)
-- welcome_sent_at audit column + stamp proc; sp_RegisterMember result set widened.
-- Idempotent. Apply against sqldb-nsl-prod AFTER db/wishlist4.sql.
-- ============================================================================
SET NOCOUNT ON;

IF COL_LENGTH('dbo.members', 'welcome_sent_at') IS NULL
    ALTER TABLE dbo.members ADD welcome_sent_at DATETIME2 NULL;
GO

-- "Who never got their welcome?" should be a seek, not a scan.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_members_welcome_pending')
    CREATE INDEX IX_members_welcome_pending ON dbo.members (created_at DESC)
        WHERE welcome_sent_at IS NULL;
GO

IF OBJECT_ID('dbo.sp_StampMemberWelcomeSent', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_StampMemberWelcomeSent;
GO
CREATE PROCEDURE dbo.sp_StampMemberWelcomeSent
    @member_number CHAR(7)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.members SET welcome_sent_at = SYSUTCDATETIME()
    WHERE  member_number = @member_number;
END;
GO
GRANT EXECUTE ON dbo.sp_StampMemberWelcomeSent TO nsl_api;
GO

-- sp_RegisterMember: same logic as db/wishlist4.sql, result sets widened with
-- created_at + welcome_sent_at so the API needs no second round trip.
IF OBJECT_ID('dbo.sp_RegisterMember', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RegisterMember;
GO
CREATE PROCEDURE dbo.sp_RegisterMember
    @first_name NVARCHAR(100),
    @last_name  NVARCHAR(100),
    @email      NVARCHAR(320),
    @phone      VARCHAR(30)   = NULL,
    @city       NVARCHAR(120) = NULL,
    @state      VARCHAR(2)    = NULL,
    @zip        VARCHAR(10)   = NULL,
    @how_heard  NVARCHAR(200) = NULL,
    @source     NVARCHAR(40)  = 'web'
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    SET @email = LOWER(LTRIM(RTRIM(@email)));
    SET @state = UPPER(NULLIF(LTRIM(RTRIM(@state)), ''));

    BEGIN TRAN;
    DECLARE @lock INT;
    EXEC @lock = sp_getapplock @Resource = 'nsl_member_number', @LockMode = 'Exclusive',
                               @LockOwner = 'Transaction', @LockTimeout = 5000;
    IF @lock < 0
    BEGIN
        ROLLBACK TRAN;
        RAISERROR('Could not reserve a member number right now — please try again.', 16, 1);
        RETURN;
    END;

    DECLARE @existing CHAR(7) = (SELECT member_number FROM dbo.members WHERE email = @email);
    IF @existing IS NOT NULL
    BEGIN
        COMMIT TRAN;
        SELECT m.member_number, CAST(1 AS BIT) AS already_registered, m.created_at, m.welcome_sent_at
        FROM dbo.members m WHERE m.member_number = @existing;
        RETURN;
    END;

    DECLARE @yy CHAR(2) = RIGHT(CAST(YEAR(SYSDATETIMEOFFSET() AT TIME ZONE 'Eastern Standard Time') AS VARCHAR(4)), 2);
    DECLARE @next INT = ISNULL((SELECT MAX(CAST(RIGHT(member_number, 5) AS INT))
                                FROM dbo.members WHERE LEFT(member_number, 2) = @yy), 0) + 1;
    DECLARE @num CHAR(7) = @yy + RIGHT('00000' + CAST(@next AS VARCHAR(5)), 5);

    INSERT INTO dbo.members (member_number, first_name, last_name, email, phone, city, state, zip, how_heard, source)
    VALUES (@num, LTRIM(RTRIM(@first_name)), LTRIM(RTRIM(@last_name)), @email,
            NULLIF(LTRIM(RTRIM(@phone)), ''), NULLIF(LTRIM(RTRIM(@city)), ''), @state,
            NULLIF(LTRIM(RTRIM(@zip)), ''), NULLIF(LTRIM(RTRIM(@how_heard)), ''), COALESCE(@source, 'web'));
    COMMIT TRAN;

    SELECT @num AS member_number, CAST(0 AS BIT) AS already_registered,
           SYSUTCDATETIME() AS created_at, CAST(NULL AS DATETIME2) AS welcome_sent_at;
END;
GO
GRANT EXECUTE ON dbo.sp_RegisterMember TO nsl_api;
GO

PRINT 'member-welcome-mail: welcome_sent_at + sp_StampMemberWelcomeSent + widened sp_RegisterMember ready.';
```

- [ ] **Step 2: List + CSV**

In `MembersFunction.cs` change `ListSql` to end `…, how_heard, source, created_at, welcome_sent_at\nFROM dbo.members ORDER BY created_at DESC`. In `ExportCsv`: header becomes `"member_number,first_name,last_name,email,phone,city,state,zip,how_heard,source,created_at,welcome_sent_at\r\n"`; add after the `createdAt` line
```csharp
            var welcomeAt = d.TryGetValue("welcome_sent_at", out var w) && w is DateTime wt ? wt.ToString("yyyy-MM-ddTHH:mm:ssZ") : "";
```
and append `CsvField(welcomeAt)` as the last array element.

- [ ] **Step 3: Build, commit (do not apply the SQL yet — Task 5)**

```powershell
dotnet build api/api.csproj
git add db/member-welcome-mail.sql api/Functions/MembersFunction.cs
git commit -m "feat(mail): welcome_sent_at column, stamp proc, widened sp_RegisterMember; list/CSV columns"
```

---

### Task 4: Send on signup + staff resend route + staff page

**Files:**
- Modify: `api/Functions/MembersFunction.cs` — constructor, `Register` (after the log line), new `ResendWelcome` function
- Modify: `staff/js/api.js:81`, `staff/js/members.js` (table), `staff/members.html` (no markup change needed unless a column header is hard-coded — it is in JS)

**Interfaces:**
- Consumes: `MailService.SendMemberWelcomeAsync`, `MemberMail` (Tasks 1–2), `sp_StampMemberWelcomeSent` (Task 3).
- Produces: `POST /api/members/{memberNumber}/resend-welcome` → `200 { sent: bool, memberNumber, welcomeSentAt }` | `404` | `409 { error }` when mail is disabled; `apiClient.resendWelcome(num)`.

- [ ] **Step 1: Inject `MailService`**

```csharp
    private readonly SqlService _sql;
    private readonly MailService _mail;
    private readonly ILogger<MembersFunction> _log;

    public MembersFunction(SqlService sql, MailService mail, ILogger<MembersFunction> log)
    {
        _sql = sql;
        _mail = mail;
        _log = log;
    }
```

- [ ] **Step 2: Call site in `Register`**

Replace the final two statements (the `// 5.` comment + `return new OkObjectResult(...)`) with:
```csharp
        // 5. A returning email gets alreadyRegistered but NOT the number: the
        // number is what people give at the register, so echoing it back to
        // anyone who types someone else's email would hand it out for free
        // (and turn this route into a "is X signed up?" oracle).
        // Built BEFORE the mail attempt — the signup has already succeeded.
        var result = new OkObjectResult(new { memberNumber = already ? null : memberNumber, alreadyRegistered = already });

        // 6. Welcome email, new members only (v1). Best effort: nothing in this
        // block may change what the visitor sees. CancellationToken.None so a
        // closed tab cannot cancel a send already in flight.
        if (!already)
        {
            try
            {
                if (await _mail.SendMemberWelcomeAsync(new MemberMail(memberNumber, first, email), CancellationToken.None))
                    await conn.ExecuteAsync("EXEC dbo.sp_StampMemberWelcomeSent @member_number = @Num", new { Num = memberNumber });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "RegisterMember: welcome-mail path threw for {Number} (signup still succeeded)", memberNumber);
            }
        }
        return result;
```

- [ ] **Step 3: Resend route (add after `ExportCsv`)**

```csharp
    /// <summary>
    /// POST /api/members/{memberNumber}/resend-welcome — staff-only by routing
    /// (staticwebapp.config.json gates /api/*). Sends the welcome template again
    /// and stamps welcome_sent_at. Who asked is logged for the audit trail.
    /// </summary>
    [Function("ResendMemberWelcome")]
    public async Task<IActionResult> ResendWelcome(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "members/{memberNumber}/resend-welcome")] HttpRequest req,
        string memberNumber,
        CancellationToken ct)
    {
        if (!_mail.Configured)
            return new ConflictObjectResult(new { error = "Email sending is turned off (MAIL_ENABLED)." });
        var num = (memberNumber ?? "").Trim();
        if (num.Length != 7 || !num.All(char.IsDigit))
            return new BadRequestObjectResult(new { error = "memberNumber must be 7 digits." });

        await using var conn = await _sql.OpenAsync(ct);
        var m = await conn.QueryFirstOrDefaultAsync(
            "SELECT member_number, first_name, email FROM dbo.members WHERE member_number = @num", new { num });
        if (m == null) return new NotFoundResult();

        var who = ClientPrincipal.UserDetails(req);
        var sent = await _mail.SendMemberWelcomeAsync(
            new MemberMail(((string)m.member_number).Trim(), (string)m.first_name, (string)m.email), CancellationToken.None);
        DateTime? stampedAt = null;
        if (sent)
        {
            await conn.ExecuteAsync("EXEC dbo.sp_StampMemberWelcomeSent @member_number = @num", new { num });
            stampedAt = DateTime.UtcNow;
        }
        _log.LogInformation("ResendMemberWelcome: {Number} by {Who} -> sent={Sent}", num, who, sent);
        return new OkObjectResult(new { sent, memberNumber = num, welcomeSentAt = stampedAt });
    }
```
(`ClientPrincipal.UserDetails(HttpRequest)` exists in `api/Services/ClientPrincipal.cs` and is used by `PalletsFunction`.)

- [ ] **Step 4: Staff page**

`staff/js/api.js` — after the `members:` line add:
```javascript
  resendWelcome:   (num)     => api('POST', `/api/members/${encodeURIComponent(num)}/resend-welcome`),
```
`staff/js/members.js` — header row gains `<th>Welcome email</th>` after `Joined`; each row gains, after the Joined cell:
```javascript
        <td>${m.welcome_sent_at
          ? new Date(m.welcome_sent_at).toLocaleDateString()
          : `<button type="button" class="btn btn-secondary resend-welcome" data-num="${esc(m.member_number)}" style="padding:4px 10px;font-size:11px;">Send welcome</button>`}</td>
```
Change the empty-state `colspan="8"` to `colspan="9"`. After the table innerHTML assignment add:
```javascript
  document.querySelectorAll('.resend-welcome').forEach(b => b.addEventListener('click', async () => {
    b.disabled = true; b.textContent = 'Sending…';
    try {
      const r = await apiClient.resendWelcome(b.dataset.num);
      toast(r.sent ? `Welcome email sent to member ${b.dataset.num}` : 'Email not sent — check MAIL settings / logs', r.sent ? 'ok' : 'err', 3500);
      await loadMembers();
    } catch (e) {
      toast(`Send failed: ${e.message}`, 'err', 4000);
      b.disabled = false; b.textContent = 'Send welcome';
    }
  }));
```

- [ ] **Step 5: Build, test, commit**

```powershell
dotnet build api/api.csproj; dotnet test api.Tests/api.Tests.csproj
git add api/Functions/MembersFunction.cs staff/js/api.js staff/js/members.js
git commit -m "feat(mail): send welcome on new signup; staff resend-welcome route + button"
```

---

### Task 5: Config (Azure side), PR, rollout

**Files:**
- Modify: `docs/superpowers/specs/2026-09-13-member-welcome-email-design.md` (runbook status notes only)

Split by who can run it. **Controller/Jeff's `az` session (non-interactive, already signed in):**

- [ ] **Step 1: App registration + service principal + secret + SWA settings (kill switch OFF)**

```powershell
$TENANT = 'd9b645c3-3587-4cd4-be9b-1a8d405c92ad'
az account show --query tenantId -o tsv          # must equal $TENANT
$app   = az ad app create --display-name 'NSL-Website-Mail' --sign-in-audience AzureADMyOrg -o json | ConvertFrom-Json
$APPID = $app.appId
az ad sp create --id $APPID -o none
$SPOID = az ad sp show --id $APPID --query id -o tsv
$SECRET = az ad app credential reset --id $APPID --display-name 'swa-stapp-nsl-website' --years 2 --query password -o tsv
az staticwebapp appsettings set --name stapp-nsl-website --resource-group rg-nsl-website --setting-names `
  "MAIL_TENANT_ID=$TENANT" "MAIL_CLIENT_ID=$APPID" "MAIL_CLIENT_SECRET=$SECRET" "MAIL_FROM=hello@northstateliquidators.com" "MAIL_ENABLED=false" -o none
"APPID=$APPID`nSPOID=$SPOID"     # record both; the secret is never printed
```
Do NOT run `az ad app permission add` / `admin-consent` (RBAC 5A union rule).

**Jeff's interactive PowerShell 7 (Exchange Online prompts for sign-in) — run as `! pwsh -File …` or paste:**

- [ ] **Step 2: Exchange scoping (5A)** — `Connect-ExchangeOnline`, create `NSL-Mail-Senders` (members: hello@ only, hidden), `New-ServicePrincipal -AppId $APPID -ObjectId $SPOID`, `New-ManagementScope 'NSL Mail Senders Scope'`, `New-ManagementRoleAssignment -Role 'Application Mail.Send' -App $APPID -CustomResourceScope 'NSL Mail Senders Scope'`, then `Test-ServicePrincipalAuthorization` for hello@ (allowed) and jeffrey.blanchard@tenantiqpro.com (NOT allowed). Exact commands: design §4 steps 3 and 5A. Wait ~30 min.

- [ ] **Step 3: Smoke test from Jeff's machine** — design §4 step 7: token with `$APPID/$SECRET`, `sendMail` as hello@ → 202; as Jeff's own mailbox → 403. Check hello@ Sent Items.

- [ ] **Step 3b: Smoke test uses the percent-encoded mailbox** — the code posts to `/users/hello%40northstateliquidators.com/sendMail` (`Uri.EscapeDataString`); the runbook's smoke test uses that exact spelling so the URL form is proven before go-live.

- [ ] **Step 4: PR** — push `feature/member-welcome-email`, `gh pr create` (base `main`; note it stacks on #14 until that merges), wait for `API unit tests` + `Build and Deploy` green. **Do not merge yet.**

- [ ] **Step 5: Apply the migration BEFORE merging** — `db/member-welcome-mail.sql` to prod (Invoke-Sqlcmd with Entra token). `MembersFunction.ListSql` and the CSV export select `welcome_sent_at`; if the code deploys first, the staff members page and CSV return 500 (`Invalid column name`). The migration is additive and harmless to the old code, so it is safe to apply early.

- [ ] **Step 6: Merge (Jeff) → deploy → go live** — with `MAIL_ENABLED=false` sign up with a personal address (row appears, no mail); flip `MAIL_ENABLED=true` (only after the Exchange scope has propagated ~30 min and the smoke test passed); sign up with a second address → email arrives, `welcome_sent_at` set, staff page shows the date; click "Send welcome" for the first address → arrives. Open that email in Outlook desktop, Outlook web, Gmail web and iOS Mail once (spec §5 #7) — the 560px div is the likeliest thing Word-rendered Outlook ignores. Rollback at any time: `MAIL_ENABLED=false`.

- [ ] **Step 6: Docs** — in the design's Status line append `— configured <date>, live <date>`; add a calendar note: secret expires 24 months from Step 1 (rotate with `az ad app credential reset` + re-set `MAIL_CLIENT_SECRET`).

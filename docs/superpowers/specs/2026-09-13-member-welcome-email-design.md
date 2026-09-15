# Member welcome email — design

**Project:** North State Liquidators storefront (`northstateliquidators.com`)
**Scope:** when someone signs up for a member number on the public site, email them the number and welcome info.
**Status:** APPROVED 2026-09-13 (Jeff) with these decisions — v1 sends the **welcome only** (no returning-member reminder; the `alreadyRegistered` path sends nothing — §2(b) is deferred, rate-limited, to a later release); scope with **RBAC for Applications (5A)**, never grant the Entra `Mail.Send` consent; **one mail identity** `NSL-Website-Mail` shared with the future Square pickup email (no `sales@`); **client secret in SWA app settings** now, bring-your-own Functions migration scheduled separately.
**Plan:** `docs/superpowers/plans/2026-09-13-member-welcome-email.md`
**Date:** 2026-09-13

---

## 0. Verified starting facts

Repo (read directly):

| Fact | Where |
|---|---|
| Signup route `POST /api/public/register` → `sp_RegisterMember`, returns `{memberNumber, alreadyRegistered}`; `memberNumber` is `null` for a returning email | `api/Functions/MembersFunction.cs` |
| `dbo.members` (`member_number CHAR(7)`, `email` UNIQUE lower-cased, `source` web \| floor \| import, `created_at`); `sp_RegisterMember` is idempotent on email under an app lock | `db/wishlist4.sql` §2, §3 |
| Grants today: `SELECT, INSERT ON dbo.members TO nsl_api`, `EXECUTE ON dbo.sp_RegisterMember` | `db/wishlist4.sql` §5 |
| The browser deliberately does **not** re-show an existing number (`JOIN_SUB_BACK`) | `js/site.js` ~line 394 |
| Stack: SWA-managed .NET 8 isolated Functions in `api/`, deployed by `Azure/static-web-apps-deploy@v1` with `api_location: 'api'` | `.github/workflows/azure-static-web-apps.yml`, `api/Program.cs`, `api/api.csproj` |
| `Azure.Identity` 1.13.1 already referenced; Storage **Queues** extension referenced but **no queue/blob/timer trigger exists anywhere** in `api/Functions/` | `api/api.csproj` + grep of `api/**/*.cs` |
| App-setting naming convention documented in a service header comment | `api/Services/SquareService.cs` |
| Staff routes are `AuthorizationLevel.Anonymous` and gated by `staticwebapp.config.json` (`/api/*` → `allowedRoles: ["authenticated"]`) | `staticwebapp.config.json`, `api/Functions/SquareFunction.cs` |
| Graph client-credentials against this tenant already works from Jeff's machine (draft creation) | `outbox/Make-M365Draft.ps1` |
| A pickup-confirmation email "via M365 / Graph" is already planned for Square checkout | `docs/SQUARE-CHECKOUT-BUILD.md` |
| **There is no test project.** `api/api.csproj` is the only csproj; no `.sln`; CI runs only the SWA deploy action | repo scan |

Live tenant/Azure (read-only checks done by the coordinator):

- `hello@` and `wholesale@` exist as **shared mailboxes** (unlicensed, `accountEnabled=false` — normal). `sales@` does **not** exist. `norm@` and `rob@` are licensed users.
- SWA `stapp-nsl-website` in `rg-nsl-website` is **Standard** SKU and has a **system-assigned managed identity** (principalId `73d537c0-02b6-4a8f-944c-f3be69c1bd2a`).
- Existing app settings include `KeyVaultUri`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` (the staff-login AAD app), `SqlConnectionString`, `SQUARE_*`.
- The only NSL app registration is **"NSL Staff Portal"** (`f4f263d4-5b82-45b1-b870-07de2f6ceffd`, no API permissions) — that is the SWA login app. **No app registration named `GraphConnector` is visible via `az ad app list`**, so nothing here depends on it.
- DNS on `northstateliquidators.com` is already correct and strict: SPF `include:spf.protection.outlook.com -all`, DMARC `p=quarantine; adkim=s; aspf=s`, DKIM selector1/selector2 CNAMEs → `tenantiqpro.onmicrosoft.com`, MX → Exchange Online.
- The subscription is **not registered for `Microsoft.Communication`**; no ACS resource exists.
- `ExchangeOnlineManagement` **3.7.1** is installed on Jeff's machine.

---

## 1. Recommendation

**Send through Microsoft Graph `sendMail` as the existing `hello@northstateliquidators.com` shared mailbox, using a NEW dedicated app registration `NSL-Website-Mail` whose `Mail.Send` is confined to that one mailbox — and do NOT reuse any existing app registration or the SWA's managed identity.** Graph wins on every axis that matters here. **Cost:** $0 — `hello@` is an unlicensed shared mailbox, which needs no license ([shared mailbox limits](https://learn.microsoft.com/microsoft-365/admin/email/about-shared-mailboxes?view=o365-worldwide#before-creating-a-shared-mailbox)), and Exchange Online's 10,000-recipients/day, 30-messages/minute ceiling ([sending limits](https://learn.microsoft.com/defender-office-365/outbound-spam-sending-limits-troubleshoot#sending-limits-in-exchange-online)) is roughly 400× the volume a storefront that signed up ~dozens of members will ever produce. **DNS work: none** — the domain already publishes SPF, DKIM and a strict `p=quarantine; adkim=s; aspf=s` DMARC pointing at Exchange Online, so mail sent this way is aligned and authenticated on day one. **Reply handling: free and correct** — the From address *is* the shared mailbox Norm and Rob already read in Outlook, replies land there by MX with no `Reply-To` trickery, and with default `saveToSentItems` a copy of every welcome email appears in `hello@`'s **Sent Items** ([sendMail reference](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0)), which is exactly the kind of "I can see what the website sent" visibility two non-technical owners need. **Blast radius is the one real hazard and it is solvable:** `Mail.Send` as an application permission means "send as any user in the organization" ([Graph permissions](https://learn.microsoft.com/graph/permissions-reference#all-permissions)), and this tenant hosts Jeff's other customers — so a brand-new registration used *only* by this site, scoped in Exchange Online to a mail-enabled security group containing `hello@` and nothing else, is mandatory, not optional. That is also precisely why the existing `GraphConnector`-style credential must not be reused: its secret lives in Jeff's user environment, it can send as any mailbox in a multi-customer tenant, and copying it into a public web app would make a website compromise a cross-customer mail compromise.

**Azure Communication Services Email is the wrong tool here**, despite being the "proper" transactional-email product. It costs $0.00025/email plus $0.00012/MB ([pricing feed behind the ACS pricing page](https://azure.microsoft.com/en-us/pricing/details/communication-services/)) — trivial, but it is not the expensive part. The expensive parts are: (1) the subscription is not even registered for `Microsoft.Communication`, so this is two new resources plus a domain verification from zero; (2) a new subscription is **locked to sending as `donotreply@<domain>`** until a quota-increase support request is approved (up to 72 hours), because custom sender usernames require "a custom domain with higher than default sending limits" ([add multiple senders](https://learn.microsoft.com/azure/communication-services/quickstarts/email/add-multiple-senders)) — so the launch-day sender would literally be `donotreply@`, the opposite of "we answer the phone"; (3) ACS is **send-only** ([quota-increase doc](https://learn.microsoft.com/azure/communication-services/concepts/email/email-quota-increase)), so replies route by the sender domain's MX — send from the apex and replies reach `hello@` fine, but send from a `mail.` subdomain with no MX and replies **bounce** with `5.7.27 Sender Address Has Null MX`; and (4) the DNS bill is real precisely because this domain is already strict — `adkim=s; aspf=s` means ACS must publish its own DKIM on the *exact* sending domain, and ACS refuses `~all` SPF while requiring `-all` ([ACS domain troubleshooting](https://learn.microsoft.com/azure/communication-services/concepts/email/email-domain-configuration-troubleshooting#2-unable-to-verify-spf-status)), which means touching a working mail domain at GoDaddy to solve a problem Graph doesn't have. Revisit ACS only if NSL ever sends true bulk marketing (thousands/day, unsubscribe handling, bounce webhooks) — that traffic should never share `hello@`'s reputation anyway.

### Correction to a prior assumption: the SWA managed identity cannot be used

The coordinator's read-only check correctly found a system-assigned managed identity on `stapp-nsl-website`, and proposed assigning Graph `Mail.Send` (`b633e1c5-b582-4048-a93e-9f11b44c7e96`) to it for a secret-free design. That would be the better design — **but it does not work for a SWA-*managed* API.** Microsoft's managed-vs-bring-your-own feature table lists **Managed identity ✕** and **Key Vault references ✕** for managed functions ([apis-functions](https://learn.microsoft.com/azure/static-web-apps/apis-functions)), and the FAQ is explicit: *"Static Web Apps supports managed identity, but it's only used to retrieve authentication secrets from Key Vault. If you need managed identity or Key Vault references in your API, use the bring your own Functions app feature."* ([SWA FAQ](https://learn.microsoft.com/azure/static-web-apps/faq)). The Key Vault article adds that KV integration is unavailable for *"static web apps using managed functions"* ([key-vault-secrets](https://learn.microsoft.com/azure/static-web-apps/key-vault-secrets)). This repo's own code says the same thing twice, independently discovered: `api/Services/SqlService.cs` — *"SWA managed Functions doesn't expose IMDS, so we ship today with SQL auth credentials in the connection string"* — and `api/Services/BlobService.cs` — *"shared-key auth (since SWA managed Functions don't expose IMDS for managed-identity auth)"*. The SWA's MI and the `KeyVaultUri` app setting are platform-level plumbing; the Functions runtime cannot reach IMDS to use them.

So: **a client secret in SWA application settings is the only viable credential today.** App settings *"are encrypted at rest"* ([application settings](https://learn.microsoft.com/en-us/azure/static-web-apps/application-settings)), which is the same protection the site's `AZURE_CLIENT_SECRET`, `SqlConnectionString` and `SQUARE_PROD_ACCESS_TOKEN` already rely on — this adds no new class of exposure. The secret-free design becomes available the day the API moves to **bring your own Functions** (a linked Function App, available on the Standard plan the SWA is already on) — at that point `MailService` needs a one-line config change and nothing else, because it already builds a `TokenCredential` rather than a bearer string. Record that as the migration trigger, not as a blocker now.

---

## 2. What the email says

Two templates, one shape. Brand voice is lifted from `index.html` / `js/site.js`: warehouse in Wake Forest NC, *"Big Inventory. Your Size. Your Price Point."*, *"We sort. We sell. We answer the phone."*, phone **(919) 526-0112**, `hello@northstateliquidators.com`, free delivery to the Raleigh Flea Market every Friday. Plain words, short sentences, no "Dear valued customer".

- **From:** `North State Liquidators <hello@northstateliquidators.com>`
- **Reply-To:** same — replies land in the `hello@` shared mailbox Norm and Rob already read in Outlook.

### (a) New member — `MemberWelcome`

**Subject:** `You're member #2600001 — welcome to North State Liquidators`

(The number is interpolated. Keep it in the subject: it is the one thing people come back to their inbox searching for.)

**Preheader** (hidden first line, ~90 chars): `Your member number is 2600001. Give it at the warehouse — save this email.`

**HTML body** — inline styles only (Outlook drops `<style>` blocks in several configurations); single column, max-width 560px; no web fonts; no images at all, so a blocked-image client loses nothing:

```html
<div style="margin:0;padding:24px 12px;background:#f6f4ef;font-family:Segoe UI,Arial,Helvetica,sans-serif;color:#1d2330;">
  <div style="display:none;max-height:0;overflow:hidden;opacity:0;">Your member number is {{MemberNumber}}. Give it at the warehouse — save this email.</div>

  <div style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e4dfd3;border-radius:10px;overflow:hidden;">

    <div style="background:#002868;padding:18px 24px;">
      <a href="https://northstateliquidators.com/" style="text-decoration:none;color:#ffffff;font-size:18px;font-weight:700;letter-spacing:0.04em;">
        NORTH <span style="color:#f2c14e;">&#9733;</span> STATE LIQUIDATORS
      </a>
      <div style="color:#c9cfe0;font-size:12px;margin-top:4px;">Wake Forest, NC &middot; Family-owned</div>
    </div>

    <div style="padding:24px;">
      <p style="margin:0 0 14px;font-size:16px;">Hi {{FirstName}} — you're in.</p>

      <div style="margin:0 0 18px;padding:16px;background:#fff8e6;border:2px solid #f2c14e;border-radius:8px;text-align:center;">
        <div style="font-size:11px;letter-spacing:0.14em;text-transform:uppercase;color:#6b6350;">Your member number</div>
        <div style="font-size:34px;font-weight:700;letter-spacing:0.06em;font-family:Consolas,Menlo,monospace;color:#002868;margin-top:4px;">{{MemberNumber}}</div>
      </div>

      <p style="margin:0 0 12px;font-size:15px;line-height:1.55;">
        <strong>What to do with it:</strong> give that number at the register when you come to
        the warehouse, or say it on the phone. That's it — it's how we know you, and it's how
        you get member pricing.
      </p>

      <p style="margin:0 0 12px;font-size:15px;line-height:1.55;">
        <strong>What happens next:</strong> when new boxes and pallets hit the floor, we email
        members first — usually once or twice a week, never daily. Photos and the full manifest
        are on every box on the site.
      </p>

      <p style="margin:0 0 12px;font-size:15px;line-height:1.55;">
        Pickup is in Wake Forest. We deliver free to the Raleigh Flea Market every Friday,
        $10 within 20 miles of the warehouse, and we ship too.
      </p>

      <p style="margin:22px 0;text-align:center;">
        <a href="https://northstateliquidators.com/shop.html?view=new"
           style="display:inline-block;background:#bf0a30;color:#ffffff;text-decoration:none;
                  padding:13px 26px;border-radius:6px;font-weight:700;font-size:15px;">
          See what's on the floor &rarr;
        </a>
      </p>

      <p style="margin:0 0 6px;font-size:15px;line-height:1.55;">Questions? Call us — we answer the phone.</p>
      <p style="margin:0;font-size:15px;line-height:1.55;">
        <a href="tel:+19195260112" style="color:#002868;font-weight:700;text-decoration:none;">(919) 526-0112</a>
        &nbsp;&middot;&nbsp;
        <a href="mailto:hello@northstateliquidators.com" style="color:#002868;text-decoration:none;">hello@northstateliquidators.com</a>
      </p>

      <p style="margin:18px 0 0;font-size:15px;">&mdash; Norm &amp; Rob</p>
    </div>

    <div style="padding:14px 24px;background:#f2efe8;border-top:1px solid #e4dfd3;font-size:12px;color:#6b6350;line-height:1.5;">
      North State Liquidators &middot; Warehouse in Wake Forest, NC (address by appointment)<br>
      You're getting this because you signed up for a member number at northstateliquidators.com.
      Reply to this email and a human will read it.
    </div>
  </div>
</div>
```

**Plain-text version** — keep it in the codebase (`MemberEmailTemplates.WelcomeText`) even though v1 sends HTML only (see §3.2):

```
Hi {{FirstName}} — you're in.

YOUR MEMBER NUMBER: {{MemberNumber}}

Give that number at the register when you come to the warehouse, or say it
on the phone. That's how we know you and how you get member pricing.

When new boxes and pallets hit the floor, we email members first — usually
once or twice a week, never daily. Photos and the full manifest are on every
box: https://northstateliquidators.com/shop.html?view=new

Pickup is in Wake Forest. Free delivery to the Raleigh Flea Market every
Friday, $10 within 20 miles, and we ship too.

Questions? Call us — we answer the phone. (919) 526-0112
hello@northstateliquidators.com

— Norm & Rob
North State Liquidators · Wake Forest, NC
```

### (b) Returning member — `MemberNumberReminder`

The website deliberately refuses to re-display an existing number (`JOIN_SUB_BACK`: *"We don't show the number again here; ask at the register and we'll look it up"*) because `POST /api/public/register` is anonymous — echoing the number back would turn the route into an "is this person a member, and what's their number?" oracle for anyone who can type an email address. **Email removes that objection:** the number goes only to the address already on file, which is the same address that established the record. So the API response stays exactly as it is (`memberNumber: null, alreadyRegistered: true`) and the number travels out-of-band. This is also the self-service "I lost my number" path, which today is a phone call to Norm.

**Subject:** `Your North State Liquidators member number: #2600001`

Same shell as (a) — same header, same yellow number card, same CTA/phone/signature/footer — with this middle:

```html
<p style="margin:0 0 14px;font-size:16px;">Hi {{FirstName}} — looks like you already have one.</p>

<!-- identical yellow number card -->

<p style="margin:0 0 12px;font-size:15px;line-height:1.55;">
  You signed up with us on {{JoinedDate}}, so we didn't make you a new number — here's the
  one you already have. Give it at the register or on the phone.
</p>
<p style="margin:0 0 12px;font-size:15px;line-height:1.55;">
  If you didn't just try to sign up on our website, you can ignore this email — nothing
  changed. Somebody typed this address into the member form, that's all.
</p>
```

**Abuse gate.** This template is the only path by which an anonymous request causes a member number to be emitted, so a scripted caller could mailbomb a known member's inbox. `RegisterMember` already caps 5/min per IP and 30/min globally per instance; `MailService` adds **one reminder per email address per hour**, enforced against `welcome_sent_at` (§3.5) — which is exactly why that column earns its keep beyond auditing.

### Sending discipline

- **Transactional only.** These two fire on signup. The weekly "new drops" blast stays Norm and Rob's CSV + Outlook job (`/api/members/export.csv`). Marketing volume is what damages a sending domain's reputation, and bulk mail needs an unsubscribe mechanism this design deliberately does not build.
- **No unsubscribe link** on these two: a transactional receipt for an action the person just took doesn't need one, and adding one implies a preference centre that doesn't exist. The footer explains why they got it and invites a reply.

---

## 3. Code design

### 3.1 Shape

One new service, one call site, one new column, one staff route. **No queue.** `api/api.csproj` references the Storage Queues extension, but there is no `QueueTrigger`, `BlobTrigger` or `TimerTrigger` anywhere in `api/Functions/` — introducing the codebase's first background-processing surface to send one email is not a trade worth making, and SWA managed functions are HTTP-trigger-only anyway ([apis-functions constraints](https://learn.microsoft.com/azure/static-web-apps/apis-functions)). Send inline, after the DB write.

```
POST /api/public/register
  └─ sp_RegisterMember              ← the transaction that matters; unchanged
  └─ build the 200 response         ← BEFORE any mail work
  └─ MailService.SendMemberWelcomeAsync(...)   ← best effort, never throws
       └─ on success: sp_StampMemberWelcomeSent
  └─ return the response that was already built
```

### 3.2 `api/Services/MailService.cs`

Mirrors `SquareService.cs`: plain `HttpClient` from `IHttpClientFactory`, config read in the constructor, an `Enabled`/`Configured` gate, an XML-doc header listing app settings. Token acquisition uses `Azure.Identity` (already referenced, 1.13.1) so the future bring-your-own-Functions migration is a config change, not a code change.

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
/// use connection strings / shared keys). If the API is ever moved to "bring
/// your own Functions", drop MAIL_CLIENT_ID/SECRET and this falls through to
/// ManagedIdentityCredential with no other change.
///
/// The app's Mail.Send is confined to MAIL_FROM in Exchange Online (see the
/// runbook). A 403 ErrorAccessDenied here means the scoping is wrong, NOT the code.
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
        _http = http; _log = log;
        Enabled   = string.Equals(cfg["MAIL_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
        From      = cfg["MAIL_FROM"] ?? "";
        _replyTo  = cfg["MAIL_REPLY_TO"] ?? From;
        _siteBase = (cfg["SITE_BASE_URL"] ?? "https://northstateliquidators.com").TrimEnd('/');

        var tenant = cfg["MAIL_TENANT_ID"];
        var client = cfg["MAIL_CLIENT_ID"];
        var secret = cfg["MAIL_CLIENT_SECRET"];
        _credential = (!string.IsNullOrEmpty(tenant) && !string.IsNullOrEmpty(client) && !string.IsNullOrEmpty(secret))
            ? new ClientSecretCredential(tenant, client, secret)
            : new ManagedIdentityCredential();   // only reachable on a BYO-Functions backend
    }

    public bool Configured => Enabled && From.Length > 0;
}
```

**Token caching:** `ClientSecretCredential` caches in-process and refreshes ahead of expiry. Call `GetTokenAsync` per send; do **not** hand-roll a cache.

**The send** — `POST https://graph.microsoft.com/v1.0/users/{MAIL_FROM}/sendMail`. The payload is the shape `outbox/Make-M365Draft.ps1` already proves against this tenant, wrapped in `message`:

```csharp
public sealed record MailResult(bool Sent, string? Error, int? StatusCode);

internal static object BuildSendMailPayload(
    string toAddress, string toName, string subject, string html, string replyTo) => new
{
    message = new
    {
        subject,
        body         = new { contentType = "HTML", content = html },
        toRecipients = new[] { new { emailAddress = new { address = toAddress, name = toName } } },
        replyTo      = new[] { new { emailAddress = new { address = replyTo } } },
    }
    // saveToSentItems is deliberately omitted: the default is true, and the Graph
    // reference says to specify it only when false. Default true is what we want —
    // a copy lands in hello@'s Sent Items so Norm and Rob see it in Outlook.
};

public async Task<MailResult> SendAsync(
    string toAddress, string toName, string subject, string html, CancellationToken ct)
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
    return new MailResult(false, Truncate(body, 500) + (retryAfter is null ? "" : $" (retry-after {retryAfter}s)"), (int)resp.StatusCode);
}
```

Graph returns **202 Accepted** with an empty body; 202 means *accepted*, not *delivered* ([sendMail reference](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0)). Treat it as "handed off", nothing more.

**On the text part.** A single Graph `sendMail` `body` is `Text` **or** `HTML`, not both. v1 ships **HTML only** — Exchange Online generates a text alternative downstream. Keep `MemberEmailTemplates.WelcomeText` anyway: it is the cheapest thing to unit-test, and it is what a future `multipart/alternative` MIME upgrade would consume if a deliverability problem ever appears. Do not build the MIME path speculatively.

### 3.3 `SendMemberWelcomeAsync`

```csharp
public sealed record MemberMail(string MemberNumber, string FirstName, string Email,
                                bool AlreadyRegistered, DateTime? JoinedAt);

/// <summary>
/// Best effort. NEVER throws — a mail problem must not turn a successful signup
/// into an error the visitor sees. Returns whether it sent, so the caller can
/// stamp welcome_sent_at.
/// </summary>
public async Task<bool> SendMemberWelcomeAsync(MemberMail m, CancellationToken ct)
{
    if (!Configured)
    {
        _log.LogInformation("MailService disabled — no welcome sent for member {Num}", m.MemberNumber);
        return false;
    }

    var (subject, html) = m.AlreadyRegistered
        ? MemberEmailTemplates.Reminder(m, _siteBase)
        : MemberEmailTemplates.Welcome(m, _siteBase);

    // One retry, transient classes only (429 / 5xx / timeout). This runs on the
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
                "Welcome mail failed for member {Num}: HTTP {Status} {Error}",
                m.MemberNumber, r.StatusCode, r.Error);
            if (!transient) return false;                 // 401/403/404 will not fix themselves
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Welcome mail attempt {N} threw for member {Num}", attempt, m.MemberNumber);
        }
        if (attempt == 1) await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
    return false;
}
```

**Log the member number, never the recipient address.** The number is enough to find the row; addresses in App Insights are a small but real data spill.

### 3.4 Call site in `MembersFunction.Register`

The only change is between the existing log line and the existing `return`. The response object is built *first*, the mail attempt is second, and the whole mail path is wrapped so nothing it does can change what the visitor sees.

```csharp
string memberNumber = ((string)row.member_number).Trim();
bool already = (bool)row.already_registered;
_log.LogInformation("RegisterMember: {Number} ({Status})", memberNumber, already ? "existing" : "new");

// Built BEFORE the mail attempt — the signup has already succeeded.
var result = new OkObjectResult(new { memberNumber = already ? null : memberNumber, alreadyRegistered = already });

try
{
    var joined   = row.created_at      as DateTime?;   // added to the proc's result set — see 3.5
    var lastSent = row.welcome_sent_at as DateTime?;

    // Anti-mailbomb gate: a reminder goes out at most once an hour per address.
    // A brand-new member always gets their welcome.
    var shouldSend = !already || lastSent is null || lastSent < DateTime.UtcNow.AddHours(-1);

    if (shouldSend && await _mail.SendMemberWelcomeAsync(
            new MailService.MemberMail(memberNumber, first, email, already, joined), CancellationToken.None))
    {
        await conn.ExecuteAsync("EXEC dbo.sp_StampMemberWelcomeSent @member_number = @Num",
                                new { Num = memberNumber });
    }
}
catch (Exception ex)
{
    // Including a SQL failure on the stamp. The member IS registered; nothing here may change that.
    _log.LogError(ex, "RegisterMember: welcome-mail path threw for {Number} (signup still succeeded)", memberNumber);
}

return result;
```

`CancellationToken.None` is deliberate: a visitor closing the tab must not cancel a send that is already in flight. Note `conn` is still open — the existing `await using var conn` covers this block.

**Latency cost.** The signup POST gains one token fetch (cached after the first request on that instance) plus one Graph round trip — roughly 150–400 ms on a request that already does a SQL round trip under an app lock. Worst case is bounded at ~18 s (8 s + 2 s + 8 s), inside SWA's **45-second** per-API-request ceiling ([apis-overview](https://learn.microsoft.com/en-us/azure/static-web-apps/apis-overview)) but longer than anyone should watch a spinner. If App Insights ever shows this path running slow in practice, **drop the retry — do not add a queue.**

### 3.5 Migration — `db/member-welcome-mail.sql`

Same idempotent style as `db/wishlist4.sql`:

```sql
-- ============================================================================
-- Member welcome mail: welcome_sent_at audit column + stamp proc.
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

-- nsl_api has SELECT, INSERT on dbo.members (db/wishlist4.sql §5). The stamp goes
-- through a proc so no UPDATE grant on the table itself is needed.
GRANT EXECUTE ON dbo.sp_StampMemberWelcomeSent TO nsl_api;
GO
```

`sp_RegisterMember` also needs its two `SELECT`s widened so the API can decide and render without a second round trip:

```sql
-- existing-member branch
SELECT @existing AS member_number, CAST(1 AS BIT) AS already_registered,
       m.created_at, m.welcome_sent_at
FROM   dbo.members m WHERE m.email = @email;

-- new-member branch
SELECT @num AS member_number, CAST(0 AS BIT) AS already_registered,
       SYSUTCDATETIME() AS created_at, CAST(NULL AS DATETIME2) AS welcome_sent_at;
```

Also add `welcome_sent_at` to `MembersFunction.ListSql`, to the CSV header and to `CsvField(...)` in `ExportMembersCsv`.

### 3.6 Staff "Resend welcome"

```
POST /api/members/{memberNumber}/resend-welcome
```

- New function in `MembersFunction`, declared `AuthorizationLevel.Anonymous` like every other staff route — `staticwebapp.config.json` already gates `/api/*` behind `allowedRoles: ["authenticated"]`, so it is staff-only by routing exactly as `ListMembers` is today.
- Loads the member by `member_number`, sends the **welcome** template (not the reminder — a staff resend means "here is your welcome again", with the full what-to-do-with-it copy), stamps `welcome_sent_at`, returns `{ sent, email, welcomeSentAt }`.
- **Bypasses** the one-per-hour gate (a human asked) but logs `ClientPrincipal.UserDetails(req)` as who asked — the same audit identity `manifest_history.changed_by` uses.
- UI: one column and one button per row in `staff/members.html` + `staff/js/members.js`, following the existing render —
  `<td>${m.welcome_sent_at ? new Date(m.welcome_sent_at).toLocaleDateString() : '<button class="btn-mini" data-resend="'+esc(m.member_number)+'">Resend</button>'}</td>` — plus
  `resendWelcome: (num) => api('POST', \`/api/members/${num}/resend-welcome\`)` in `apiClient` (`staff/js/api.js`).
- That column **is** the operational dashboard: a `source = 'web'` row with `welcome_sent_at IS NULL` is someone the system failed.

### 3.7 `Program.cs`

```csharp
builder.Services.AddSingleton<MailService>();
```

Singleton, like every other service here — the credential caches tokens and must not be rebuilt per request.

### 3.8 What to unit-test

**There is no test project in this repo today** (only `api/api.csproj`; no `.sln`; CI runs only the deploy action). This work should create `api.Tests/` — xUnit, `net8.0`, `ProjectReference` to `api/api.csproj` — and add a `dotnet test` step to `.github/workflows/azure-static-web-apps.yml` before the deploy step. Keep every test a pure function call: no SQL, no Graph, no host.

| Test | Asserts |
|---|---|
| `Welcome_RendersNumberAndName` | subject and HTML both contain `2600001` and the first name |
| `Welcome_EscapesHostileName` | a `first_name` of `<script>x</script>` (free text typed by an anonymous visitor — the one real injection surface) comes out HTML-escaped in body **and** subject |
| `Reminder_ContainsNumberAndJoinDate` | the reminder is the only path that emits an existing number — assert it does, and that it carries the join date |
| `Reminder_DoesNotUseWelcomeOpening` | reminder copy does not contain "you're in" |
| `Templates_LeaveNoUnreplacedTokens` | no `{{` survives in any rendered output |
| `Payload_MatchesGraphSendMailShape` | serialize `BuildSendMailPayload(...)` and assert `message.body.contentType == "HTML"`, `message.toRecipients[0].emailAddress.address`, `message.replyTo[0]...`, **and that `toRecipients` serialises as a JSON array** — the same check `outbox/Make-M365Draft.ps1` makes by hand (`if ($json -notmatch '"toRecipients":\[') { throw }`), because a one-element array collapsing to an object is the classic failure |
| `Payload_OmitsSaveToSentItems` | the property is absent, so Graph's default (true) applies |
| `ShouldSend_RefusesReminderWithinAnHour` | gate logic with `welcome_sent_at` 10 minutes ago |
| `ShouldSend_AllowsNewMemberAlways` | new signup always sends |
| `MailService_DisabledByDefault` | with `MAIL_ENABLED` unset, `Configured == false` and `SendMemberWelcomeAsync` returns false without any HTTP |

Refactor that makes all of this possible: rendering lives in a **static** `MemberEmailTemplates` returning `(string Subject, string Html)` (+ a `WelcomeText`), and the payload builder is the static `BuildSendMailPayload` above. Neither touches `IConfiguration` or `HttpClient`.

---

## 4. Configuration runbook

Run once, by Jeff, signed in to the **TenantIQ Pro** tenant (`d9b645c3-3587-4cd4-be9b-1a8d405c92ad`) as Global Admin. PowerShell 7. `ExchangeOnlineManagement` 3.7.1 is already installed.

> **Read this first.** Step 5 has two variants. **5A (RBAC for Applications) is the recommended one** — Microsoft's own documentation now says *"App Access Policies has been replaced by Role Based Access Control for Applications… New access configuration should not use Application Access Policies since this feature will have deprecation announced in the future"* ([application-access-policies](https://learn.microsoft.com/exchange/permissions-exo/application-access-policies)). **5B (Application Access Policy)** is the long-established fallback; use it only if 5A misbehaves. **Do exactly one of them** — and note the union rule in 5A, which is the single easiest way to get this wrong.

### Step 0 — preflight (read-only; confirm before changing anything)

```powershell
# 0.1 Correct tenant?
az account show --query "{tenant:tenantId, name:name, sub:id}" -o json
#   tenant MUST be d9b645c3-3587-4cd4-be9b-1a8d405c92ad

# 0.2 hello@ exists and is a shared mailbox
Connect-ExchangeOnline -ShowBanner:$false
Get-Mailbox -Identity hello@northstateliquidators.com |
    Format-List Name,PrimarySmtpAddress,RecipientTypeDetails,ExchangeGuid
#   RecipientTypeDetails MUST be SharedMailbox

# 0.3 Mail authentication is already in place (expect: all four present)
nslookup -type=txt northstateliquidators.com
nslookup -type=txt _dmarc.northstateliquidators.com
nslookup -type=cname selector1._domainkey.northstateliquidators.com
nslookup -type=cname selector2._domainkey.northstateliquidators.com
#   SPF   : v=spf1 include:spf.protection.outlook.com -all
#   DMARC : v=DMARC1; p=quarantine; adkim=s; aspf=s; ...
#   DKIM  : both selectors CNAME into tenantiqpro.onmicrosoft.com
# Authoritative source for the DKIM targets (never hand-build them):
Get-DkimSigningConfig -Identity northstateliquidators.com |
    Format-List Name,Enabled,Status,Selector1CNAME,Selector2CNAME
```

If 0.3 is green (it was at last check), **no DNS work is required for this project at all.**

### Step 1 — create the dedicated app registration

```powershell
$TENANT = 'd9b645c3-3587-4cd4-be9b-1a8d405c92ad'

$app = az ad app create `
  --display-name 'NSL-Website-Mail' `
  --sign-in-audience AzureADMyOrg `
  --notes 'Sends member welcome email from hello@northstateliquidators.com. Scoped in Exchange Online to the NSL-Mail-Senders group. Used only by stapp-nsl-website.' `
  -o json | ConvertFrom-Json

$APPID = $app.appId
az ad sp create --id $APPID -o none          # the enterprise-app / service principal
$SPOID = az ad sp show --id $APPID --query id -o tsv

"APPID  = $APPID"
"SP OID = $SPOID"
```

Record both. `APPID` goes in `MAIL_CLIENT_ID`; `SPOID` is needed by step 5A.

### Step 2 — client secret (24 months)

```powershell
$SECRET = az ad app credential reset `
  --id $APPID `
  --display-name 'swa-stapp-nsl-website' `
  --years 2 `
  --query password -o tsv

# Do NOT echo this into a transcript, a file, or a chat. It goes straight into
# step 6 in the same session. Calendar reminder: expires in 24 months.
```

> `az ad app credential reset` **replaces** existing credentials on the app. On a brand-new app there are none, so this is safe here — never run it against an app that already has a credential in use.

### Step 3 — mail-enabled security group `NSL-Mail-Senders`

This group is **required**, not cosmetic: `New-ApplicationAccessPolicy -PolicyScopeGroupId` explicitly rejects shared mailboxes — *"If you need to scope the policy to shared mailboxes, you can add the shared mailboxes as members of a mail-enabled security group"* ([New-ApplicationAccessPolicy](https://learn.microsoft.com/powershell/module/exchangepowershell/new-applicationaccesspolicy?view=exchange-ps)). A plain Entra security group does not work either — only `MailUniversalSecurityGroup` is a valid recipient type.

```powershell
Connect-ExchangeOnline -ShowBanner:$false

New-DistributionGroup `
  -Name 'NSL-Mail-Senders' `
  -DisplayName 'NSL Mail Senders (app scope)' `
  -Alias 'nsl-mail-senders' `
  -PrimarySmtpAddress 'nsl-mail-senders@northstateliquidators.com' `
  -Type Security `
  -Members 'hello@northstateliquidators.com' `
  -MemberJoinRestriction Closed `
  -MemberDepartRestriction Closed

# Hide it from the address book — it is plumbing, not a distribution list.
Set-DistributionGroup -Identity 'NSL-Mail-Senders' -HiddenFromAddressListsEnabled $true

# Verify: exactly ONE member, and it is hello@
Get-DistributionGroupMember -Identity 'NSL-Mail-Senders' |
    Format-Table Name,PrimarySmtpAddress,RecipientType
```

**The security of this whole design is that membership list.** Anything added to this group becomes a mailbox the website can send as.

### Step 4 — do NOT consent Mail.Send in Entra (if using 5A)

Under RBAC for Applications the Entra permission is deliberately **not** granted. Microsoft: *"the assigned permissions are a **union** operation on the permissions from Microsoft Entra ID and the permissions assigned in Exchange Online RBAC"* ([application-rbac](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac)). Leaving a tenant-wide `Mail.Send` consent in place while adding an RBAC scope gives you **zero** scoping — the app can still send as anyone. Skip straight to 5A.

(If you choose **5B**, the Entra consent *is* required — the commands are inside 5B.)

### Step 5A — scope with RBAC for Applications ★ recommended

```powershell
Connect-ExchangeOnline -ShowBanner:$false

# 5A.1 Pointer object in Exchange to the Entra service principal.
#      ObjectId is the ENTERPRISE APPLICATION object id ($SPOID from step 1),
#      not the app registration's object id.
New-ServicePrincipal -AppId $APPID -ObjectId $SPOID -DisplayName 'NSL-Website-Mail'

# 5A.2 Management scope = "members of NSL-Mail-Senders" (direct members only —
#      nested groups are out of scope).
$grp = Get-DistributionGroup -Identity 'NSL-Mail-Senders'
New-ManagementScope -Name 'NSL Mail Senders Scope' `
  -RecipientRestrictionFilter "MemberOfGroup -eq '$($grp.DistinguishedName)'"

# 5A.3 Assign Mail.Send, scoped.
New-ManagementRoleAssignment `
  -Name 'NSL-Website-Mail to NSL Mail Senders' `
  -Role 'Application Mail.Send' `
  -App $APPID `
  -CustomResourceScope 'NSL Mail Senders Scope'

# 5A.4 Verify — this cmdlet bypasses the permission cache.
Test-ServicePrincipalAuthorization -Identity $APPID -Resource 'hello@northstateliquidators.com' | Format-Table
#   expect the Mail.Send role to come back as in-scope / allowed

Test-ServicePrincipalAuthorization -Identity $APPID -Resource 'jeffrey.blanchard@tenantiqpro.com' | Format-Table
#   expect NOT in scope — this is the blast-radius proof. If this one passes, STOP
#   and fix the scope before setting MAIL_ENABLED=true.
```

**Propagation:** *"Changes to app permissions are subject to cache maintenance that varies between 30 minutes and 2 hours depending on the app's recent usage. The cache of an app with no inbound calls to APIs is reset after 30 minutes"* ([application-rbac](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac)). A brand-new app has made no calls, so **~30 minutes** is the realistic wait. `Test-ServicePrincipalAuthorization` bypasses the cache and will look right immediately — that is not proof the live API call will work yet.

Prerequisites for these cmdlets: membership of **Organization Management** in EXO and the **Exchange Administrator** role in Entra (same doc).

### Step 5B — fallback: Application Access Policy

Use only if 5A does not work (e.g. Graph rejects the token before it reaches Exchange). 5B requires the Entra consent that 5A deliberately omits.

```powershell
# 5B.1 Grant + admin-consent Mail.Send (application).
#      00000003-0000-0000-c000-000000000000 = Microsoft Graph
#      b633e1c5-b582-4048-a93e-9f11b44c7e96 = Mail.Send (Role/application)
az ad app permission add --id $APPID `
  --api 00000003-0000-0000-c000-000000000000 `
  --api-permissions b633e1c5-b582-4048-a93e-9f11b44c7e96=Role

az ad app permission admin-consent --id $APPID
az ad app permission list-grants --id $APPID -o table     # confirm it stuck

# 5B.2 Confine it to the group from step 3.
Connect-ExchangeOnline -ShowBanner:$false
New-ApplicationAccessPolicy `
  -AppId $APPID `
  -PolicyScopeGroupId 'nsl-mail-senders@northstateliquidators.com' `
  -AccessRight RestrictAccess `
  -Description 'NSL website may send only as mailboxes in NSL-Mail-Senders (hello@).'

# 5B.3 Verify — must be allowed for hello@ and denied for anything else.
Test-ApplicationAccessPolicy -Identity 'hello@northstateliquidators.com'   -AppId $APPID
Test-ApplicationAccessPolicy -Identity 'jeffrey.blanchard@tenantiqpro.com' -AppId $APPID
Test-ApplicationAccessPolicy -Identity 'norm@northstateliquidators.com'    -AppId $APPID
#   hello@  -> access granted;  the other two -> denied.
#   (The result property/value names are not documented on Learn; read the object
#    that comes back rather than assuming a field name.)
```

**Propagation: allow a full hour.** Microsoft: *"Changes to application access policies can take longer than 1 hour to take effect in Microsoft Graph REST API calls, **even when Test-ApplicationAccessPolicy shows positive results**"* ([application-access-policies](https://learn.microsoft.com/exchange/permissions-exo/application-access-policies#configure-applicationaccesspolicy)). Do not set `MAIL_ENABLED=true` before that hour is up — a denied call returns `403 ErrorAccessDenied`, *"Access to OData is disabled."*

### Step 6 — SWA application settings

`--setting-names` takes space-separated `key=value` pairs ([az staticwebapp appsettings](https://learn.microsoft.com/en-us/cli/azure/staticwebapp/appsettings)). Setting names may contain only alphanumerics, `.` and `_` ([application settings](https://learn.microsoft.com/en-us/azure/static-web-apps/application-settings)) — all five names below comply, and none collides with a reserved SWA/Functions prefix.

```powershell
# Ship with the kill switch OFF. Nothing sends until step 8 flips it.
az staticwebapp appsettings set `
  --name stapp-nsl-website `
  --resource-group rg-nsl-website `
  --setting-names `
    "MAIL_TENANT_ID=$TENANT" `
    "MAIL_CLIENT_ID=$APPID" `
    "MAIL_CLIENT_SECRET=$SECRET" `
    "MAIL_FROM=hello@northstateliquidators.com" `
    "MAIL_ENABLED=false"

# Confirm the names landed (values are masked in list output).
az staticwebapp appsettings list --name stapp-nsl-website --resource-group rg-nsl-website -o table
```

> `az staticwebapp appsettings set` **merges** — existing settings (`SqlConnectionString`, `SQUARE_*`, `AZURE_CLIENT_*`) are untouched. Settings are copied to staging and production environments, and are encrypted at rest.

### Step 7 — smoke test from Jeff's machine (before trusting the site)

Same credential, same endpoint, same payload as the code — proves the scoping without deploying anything:

```powershell
$t = Invoke-RestMethod -Method Post `
  -Uri "https://login.microsoftonline.com/$TENANT/oauth2/v2.0/token" `
  -Body @{ grant_type='client_credentials'; client_id=$APPID; client_secret=$SECRET
           scope='https://graph.microsoft.com/.default' }

$payload = @{ message = @{
    subject = 'NSL mail smoke test'
    body    = @{ contentType='HTML'; content='<p>If you can read this, NSL-Website-Mail works.</p>' }
    toRecipients = ,@{ emailAddress = @{ address = 'jeffrey.blanchard@tenantiqpro.com' } }
}} | ConvertTo-Json -Depth 8 -Compress

# EXPECT 202 (no output) — sending AS hello@
Invoke-RestMethod -Method Post `
  -Uri 'https://graph.microsoft.com/v1.0/users/hello@northstateliquidators.com/sendMail' `
  -Headers @{ Authorization = "Bearer $($t.access_token)" } `
  -ContentType 'application/json; charset=utf-8' `
  -Body ([Text.Encoding]::UTF8.GetBytes($payload))

# EXPECT 403 ErrorAccessDenied — sending AS a mailbox outside the group.
# If THIS succeeds, the scope is not working. Stop and fix step 5.
Invoke-RestMethod -Method Post `
  -Uri 'https://graph.microsoft.com/v1.0/users/jeffrey.blanchard@tenantiqpro.com/sendMail' `
  -Headers @{ Authorization = "Bearer $($t.access_token)" } `
  -ContentType 'application/json; charset=utf-8' `
  -Body ([Text.Encoding]::UTF8.GetBytes($payload))
```

Then check `hello@`'s **Sent Items** in Outlook — the copy should be there (default `saveToSentItems`), which is also how Norm and Rob will see live traffic.

### Step 8 — go live

1. Apply `db/member-welcome-mail.sql` to `sqldb-nsl-prod`.
2. Merge and deploy the code (push to `main`).
3. Sign up with a personal address on the live site; confirm the email arrives and the row shows `welcome_sent_at`.
4. Flip the switch:
   ```powershell
   az staticwebapp appsettings set --name stapp-nsl-website --resource-group rg-nsl-website `
     --setting-names "MAIL_ENABLED=true"
   ```
5. Sign up again with the *same* address — confirm the **reminder** template arrives with the existing number, and that a second attempt within the hour sends nothing.
6. Calendar: **secret expires in 24 months** — rotate with `az ad app credential reset --id $APPID --years 2` then re-run step 6 for `MAIL_CLIENT_SECRET` only.

**Rollback at any point:** `MAIL_ENABLED=false`. Signups keep working; only the email stops.

---

## 5. Failure modes

| # | Failure | Symptom | Surfaced as | What happens to the signup | Fix |
|---|---|---|---|---|---|
| 1 | **Secret expired / wrong** | `ClientSecretCredential` throws `AuthenticationFailedException` (AADSTS7000215 invalid client secret, AADSTS700082 expired) | `LogWarning` from the attempt loop + `LogError` "welcome-mail path threw" | **Unaffected** — 200 with the number | Rotate secret (step 2), re-run step 6 |
| 2 | **Token endpoint unreachable / transient 5xx** | Timeout or 5xx from login.microsoftonline.com or Graph | `LogWarning` per attempt; one retry after 2 s | Unaffected | Self-heals; `welcome_sent_at` stays NULL → staff Resend |
| 3 | **429 throttling** | `429` + `Retry-After`. Two ceilings: Exchange **30 messages/minute** per mailbox and **10,000 recipients/day** ([sending limits](https://learn.microsoft.com/defender-office-365/outbound-spam-sending-limits-troubleshoot#sending-limits-in-exchange-online)); Graph Outlook **10,000 requests/10 min** and **4 concurrent** per app+mailbox ([throttling-limits](https://learn.microsoft.com/en-us/graph/throttling-limits)) | `LogWarning` with the `Retry-After` value captured in `MailResult.Error` | Unaffected | Practically unreachable at signup volume; if it ever fires, the global 30/min signup cap in `RegisterMember` is the real backstop. Never batch-send from this path |
| 4 | **Policy denial** | `403` with `{"error":{"code":"ErrorAccessDenied","message":"Access to OData is disabled."}}` | `LogError` (non-transient → no retry) | Unaffected | Almost always: (a) the hour hasn't elapsed after 5B, (b) `hello@` isn't in `NSL-Mail-Senders`, or (c) **5A was used but the Entra consent was left in place / removed wrongly** — re-run the step-7 403 probe |
| 5 | **Mailbox wrong or not a mailbox** | `404 ErrorInvalidUser` / `MailboxNotEnabledForRESTAPI`; typo in `MAIL_FROM` | `LogError` | Unaffected | A shared mailbox needs **no license** for this — do not "fix" it by assigning one. Confirm with step 0.2 |
| 6 | **Mail accepted but never delivered** | Graph returned **202** (accepted ≠ delivered) and nothing arrives | `welcome_sent_at` is stamped but the member says they got nothing | Unaffected | Check `hello@` Sent Items (the copy proves it left) and run an Exchange message trace. This is the one case where `welcome_sent_at` lies — treat it as "handed to Exchange" |
| 7 | **HTML renders badly** (Outlook desktop) | Broken spacing, dropped rounded corners, ignored `<style>` | Visual only | Unaffected | Already mitigated: inline styles only, single column, no images, no web fonts, ≤560px. Test once in Outlook desktop + Outlook web + Gmail web + iOS Mail before go-live |
| 8 | **Spam foldering** | Members report it landed in junk | Not observable server-side | Unaffected | Domain is already SPF+DKIM aligned under `p=quarantine; adkim=s; aspf=s`, and the From domain matches the MX — this is about as good as it gets. Keep marketing off this mailbox; keep the plain-language footer that says why they're receiving it |
| 9 | **Signup mailbombing** | Repeated `alreadyRegistered` signups for one address | `LogWarning` from the existing rate limiter | Unaffected | One reminder per address per hour (§3.4) plus the existing 5/min-per-IP and 30/min-global caps |
| 10 | **Stamp write fails** after a successful send | Mail went out, `welcome_sent_at` still NULL | `LogError` "welcome-mail path threw" | Unaffected | Worst case a duplicate on the next signup attempt for the same address — acceptable; a failed stamp must never roll back a delivered email |
| 11 | **Slow mail path** | Signup POST visibly slow | App Insights duration on `RegisterMember` | Bounded ~18 s, inside SWA's 45 s API ceiling | Drop the retry. **Do not add a queue** (SWA managed functions are HTTP-trigger-only) |

**The single operational signal:** `SELECT member_number, email, created_at FROM dbo.members WHERE source = 'web' AND welcome_sent_at IS NULL ORDER BY created_at DESC` — surfaced as the empty "Welcome sent" cell with a **Resend** button on `staff/members.html`. Norm and Rob never need to read a log; they see a blank cell and click a button.

---

## 6. Open questions for Jeff

1. **Reminder emails to returning signups — yes or no?** §2(b) argues the number is safe to send to the address already on file, and it kills the "I lost my number" phone call. But it means an anonymous form submission can cause mail to a third party's inbox (rate-limited to one/hour). Ship it, or ship welcome-only for v1?
2. **RBAC for Applications (5A) or Application Access Policy (5B)?** Microsoft says new configuration should use RBAC and that AAP will get a deprecation announcement — but AAP is two commands and is what most published guidance still shows. The design recommends 5A; confirm before the runbook is executed, because the two paths differ on whether the Entra consent is granted at all.
3. **Does the Square pickup-confirmation email (`docs/SQUARE-CHECKOUT-BUILD.md`) share this `MailService` and this app registration?** If yes — recommended — then `NSL-Website-Mail` is the site's one mail identity and `MailService` gets a second template, no new setup. If it should send from `sales@`, note that `sales@` **does not currently exist** and would need creating plus adding to `NSL-Mail-Senders`.
4. **How much is the secret-free design worth?** Managed identity is impossible on SWA-*managed* Functions, but the SWA is already on Standard, so moving `api/` to a linked "bring your own" Function App would remove this secret *and* the SQL credential *and* the storage shared key, and would unlock Key Vault references — at the cost of a second deployable and a real migration. Worth scheduling, or is an encrypted-at-rest app setting fine for now?

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

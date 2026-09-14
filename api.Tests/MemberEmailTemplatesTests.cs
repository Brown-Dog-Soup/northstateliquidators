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

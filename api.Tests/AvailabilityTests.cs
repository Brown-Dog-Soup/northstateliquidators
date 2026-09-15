using NSL.Api.Services;
using Xunit;

/// <summary>
/// The ONE definition of "can this order still sell this box" (spec §4). These
/// are the rules the money path leans on: a public cart link may only sell a
/// live, unarchived, non-ghost box that no wholesale invoice has reserved; an
/// invoice order may sell its own (drafted) box unless somebody sold it first.
/// </summary>
public class AvailabilityTests
{
    [Fact] public void Link_live_clean_box_is_available()
        => Assert.True(Availability.ForLink("live", null, false, null));

    [Theory]
    [InlineData("draft")] [InlineData("sold")] [InlineData("ghost")] [InlineData(null)]
    public void Link_non_live_states_are_unavailable(string? state)
        => Assert.False(Availability.ForLink(state, null, false, null));

    [Fact] public void Link_archived_is_unavailable()
        => Assert.False(Availability.ForLink("live", DateTime.UtcNow, false, null));

    [Fact] public void Link_ghost_flag_is_unavailable()
        => Assert.False(Availability.ForLink("live", null, true, null));

    [Fact] public void Link_invoiced_box_is_unavailable()
        => Assert.False(Availability.ForLink("live", null, false, "INV1"));

    [Theory]
    [InlineData("draft", true)] [InlineData("live", true)] [InlineData("sold", false)]
    public void Invoice_only_sold_blocks(string state, bool expected)
        => Assert.Equal(expected, Availability.ForInvoice(state));

    [Fact] public void For_dispatches_on_kind()
    {
        Assert.True(Availability.For("invoice", "draft", null, false, "INV1"));
        Assert.False(Availability.For("link", "draft", null, false, "INV1"));
    }
}

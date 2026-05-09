using Bunit;
using FluentAssertions;
using Heimdall.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Web.Tests.Components.Pages.Admin;

/// <summary>
/// bUnit tests for the Phase 3.6 step 11 admin "Ticket access" landing page
/// (<c>/admin/access</c>). Validates the form-driven navigation that ferries
/// the operator to <c>/admin/access/ticket/{id}</c>.
/// </summary>
public class TicketAccessIndexTests : BunitContext
{
    [Fact]
    public void Should_RenderEmptyForm_When_PageInitializes()
    {
        var cut = Render<TicketAccessIndex>();

        cut.Find("#ticket-id").Should().NotBeNull();
        // Submit button is disabled until a positive id is bound.
        cut.Find("button[type='submit']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Should_NavigateToTicketAccess_When_ValidIdSubmitted()
    {
        var cut = Render<TicketAccessIndex>();

        cut.Find("#ticket-id").Change("42");
        cut.Find("form").Submit();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().EndWith("/admin/access/ticket/42");
    }

    [Fact]
    public void Should_NotNavigate_When_IdIsZeroOrNegative()
    {
        var cut = Render<TicketAccessIndex>();
        var startingUri = Services.GetRequiredService<NavigationManager>().Uri;

        cut.Find("#ticket-id").Change("0");
        // Submit handler returns immediately when the id is non-positive; the
        // disabled-on-submit guard in the markup is independently exercised
        // by Should_RenderEmptyForm_When_PageInitializes.
        cut.Find("form").Submit();

        Services.GetRequiredService<NavigationManager>().Uri.Should().Be(startingUri);
    }
}

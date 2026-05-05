using Bunit;
using FluentAssertions;
using Heimdall.Web.Components.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Web.Tests.Components.Authentication;

/// <summary>
/// bUnit tests for <see cref="RedirectToLogin"/> (Phase 1 step 9 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3).
/// </summary>
public class RedirectToLoginTests : BunitContext
{
    [Fact]
    public void Should_NavigateToLoginWithReturnUrl_When_Rendered()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/tickets");

        Render<RedirectToLogin>();

        // Uri.EscapeDataString("/tickets") => "%2Ftickets".
        nav.Uri.Should().EndWith("login?returnUrl=%2Ftickets");
    }

    [Fact]
    public void Should_EncodeReturnUrl_When_OriginalPathContainsSpecialChars()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/tickets?id=abc%20def&other=a b");

        Render<RedirectToLogin>();

        // Path-and-query of the source URI is "/tickets?id=abc%20def&other=a%20b"
        // (NavigationManager normalises the unescaped space). Uri.EscapeDataString
        // then percent-encodes the '?', '=', '&', and the embedded '%' itself.
        nav.Uri.Should().Contain("login?returnUrl=");
        nav.Uri.Should().Contain("%3F");
        nav.Uri.Should().Contain("%3D");
        nav.Uri.Should().Contain("%26");
        nav.Uri.Should().Contain("%2520");
        nav.Uri.Should().NotContain(" ");
    }
}

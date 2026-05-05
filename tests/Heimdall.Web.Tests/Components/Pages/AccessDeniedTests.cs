using Bunit;
using FluentAssertions;
using Heimdall.Web.Components.Pages;

namespace Heimdall.Web.Tests.Components.Pages;

/// <summary>
/// bUnit tests for <see cref="AccessDenied"/> (Phase 1 step 9 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3).
/// </summary>
public class AccessDeniedTests : BunitContext
{
    [Fact]
    public void Should_RenderAccessDeniedContent_When_Rendered()
    {
        var cut = Render<AccessDenied>();

        cut.Markup.Should().Contain("Access denied");
        cut.Markup.Should().Contain("permission");
        cut.Find("a.btn-primary").GetAttribute("href").Should().Be("/");
    }
}

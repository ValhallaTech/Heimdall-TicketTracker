using Bunit;
using FluentAssertions;
using Heimdall.Web.Components.Pages;

namespace Heimdall.Web.Tests.Components.Pages;

/// <summary>
/// Basic markup checks for the static "check your email" pages introduced
/// alongside the forgot-password and registration flows in Phase 1 step 10.
/// </summary>
public class ForgotPasswordConfirmationTests : BunitContext
{
    [Fact]
    public void Should_RenderCheckYourEmailContent_When_Rendered()
    {
        var cut = Render<ForgotPasswordConfirmation>();

        cut.Markup.Should().Contain("Check your email");
        cut.Markup.Should().Contain("/login");
    }
}

public class RegisterConfirmationTests : BunitContext
{
    [Fact]
    public void Should_RenderCheckYourEmailContent_When_Rendered()
    {
        var cut = Render<RegisterConfirmation>();

        cut.Markup.Should().Contain("Check your email");
        cut.Markup.Should().Contain("/login");
    }
}

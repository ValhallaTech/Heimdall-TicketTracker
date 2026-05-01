using Bunit;
using FluentAssertions;
using Heimdall.Web.Components.Pages;

namespace Heimdall.Web.Tests.Components.Pages;

public class SplashTests : BunitContext
{
    [Fact]
    public void Should_RenderHeroAndCta_When_Rendered()
    {
        var cut = Render<Splash>();

        cut.Markup.Should().Contain("Heimdall TicketTracker");
        cut.Markup.Should().Contain("/tickets");
        cut.Find("a.splash-cta").TextContent.Should().Contain("Open Tickets");
    }
}

public class NotFoundTests : BunitContext
{
    [Fact]
    public void Should_RenderNotFoundContent_When_Rendered()
    {
        var cut = Render<NotFound>();

        cut.Markup.Should().Contain("404");
        cut.Markup.Should().Contain("Page not found");
        cut.Find("a.btn-primary").GetAttribute("href").Should().Be("/tickets");
    }
}

public class ErrorTests : BunitContext
{
    [Fact]
    public void Should_RenderErrorMessage_When_Rendered()
    {
        var cut = Render<Error>();

        cut.Markup.Should().Contain("An error occurred");
        cut.Markup.Should().Contain("ASPNETCORE_ENVIRONMENT");
    }

    [Fact]
    public void Should_NotRenderRequestId_When_NoActivityOrHttpContext()
    {
        var cut = Render<Error>();

        cut.Markup.Should().NotContain("Request ID");
    }

    [Fact]
    public void Should_RenderRequestId_When_ActivityCurrentIsSet()
    {
        using var activity = new System.Diagnostics.Activity("test").Start();
        try
        {
            var cut = Render<Error>();
            cut.Markup.Should().Contain("Request ID");
        }
        finally
        {
            activity.Stop();
        }
    }
}

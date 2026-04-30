using Bunit;
using FluentAssertions;
using Heimdall.Web.Components.Layout;
using Microsoft.AspNetCore.Components;

namespace Heimdall.Web.Tests.Components.Layout;

public class MainLayoutTests : BunitContext
{
    [Fact]
    public void Should_RenderNavbarAndBody_When_Rendered()
    {
        var cut = Render<MainLayout>(parameters => parameters.Add(p => p.Body, (RenderFragment)(b =>
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "id", "test-body");
            b.AddContent(2, "hello");
            b.CloseElement();
        })));

        cut.Markup.Should().Contain("Heimdall TicketTracker");
        cut.Markup.Should().Contain("theme-toggle");
        cut.Find("#test-body").TextContent.Should().Be("hello");
    }
}

public class ReconnectModalTests : BunitContext
{
    [Fact]
    public void Should_RenderReconnectDialog_When_Rendered()
    {
        var cut = Render<ReconnectModal>();
        cut.Markup.Should().Contain("Rejoining the server");
    }
}

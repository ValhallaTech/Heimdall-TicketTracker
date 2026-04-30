using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace Heimdall.Web.Tests;

public class SanityTests : BunitContext
{
    [Fact]
    public void RenderFragment_ShouldRenderExpectedMarkup()
    {
        // Arrange
        RenderFragment fragment = builder =>
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, "Hello");
            builder.CloseElement();
        };

        // Act
        var cut = Render(fragment);

        // Assert
        cut.Markup.Should().Be("<p>Hello</p>");
    }
}

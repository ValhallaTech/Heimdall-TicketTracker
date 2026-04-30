using FluentAssertions;

namespace Heimdall.Core.Tests;

public class SanityTests
{
    [Fact]
    public void True_ShouldBeTrue()
    {
        // Arrange
        var value = true;

        // Act & Assert
        value.Should().BeTrue();
    }
}

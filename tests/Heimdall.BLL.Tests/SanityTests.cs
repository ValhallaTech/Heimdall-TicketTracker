using FluentAssertions;

namespace Heimdall.BLL.Tests;

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

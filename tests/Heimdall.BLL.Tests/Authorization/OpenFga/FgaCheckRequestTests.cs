using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;

namespace Heimdall.BLL.Tests.Authorization.OpenFga;

/// <summary>
/// Unit tests for the <see cref="FgaCheckRequest"/> / <see cref="FgaListObjectsRequest"/>
/// records — they're plain data carriers, so the only contract worth pinning is
/// the default consistency + value-equality semantics promised by <c>record</c>.
/// </summary>
public class FgaCheckRequestTests
{
    [Fact]
    public void FgaCheckRequest_Should_DefaultConsistencyToMinimizeLatency()
    {
        var request = new FgaCheckRequest("user:1", "view", "ticket:1");

        request.Consistency.Should().Be(FgaConsistency.MinimizeLatency);
    }

    [Fact]
    public void FgaCheckRequest_Should_HaveValueEquality()
    {
        var a = new FgaCheckRequest("user:1", "view", "ticket:1", FgaConsistency.HigherConsistency);
        var b = new FgaCheckRequest("user:1", "view", "ticket:1", FgaConsistency.HigherConsistency);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void FgaListObjectsRequest_Should_DefaultConsistencyToMinimizeLatency()
    {
        var request = new FgaListObjectsRequest("user:1", "view", "ticket");

        request.Consistency.Should().Be(FgaConsistency.MinimizeLatency);
    }

    [Theory]
    [InlineData(FgaConsistency.MinimizeLatency, 0)]
    [InlineData(FgaConsistency.HigherConsistency, 1)]
    public void FgaConsistency_Should_HaveStableUnderlyingValues(FgaConsistency value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}

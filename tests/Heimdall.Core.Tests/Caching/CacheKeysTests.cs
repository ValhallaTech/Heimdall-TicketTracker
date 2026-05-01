using FluentAssertions;
using Heimdall.Core.Caching;

namespace Heimdall.Core.Tests.Caching;

public class CacheKeysTests
{
    [Fact]
    public void Should_DefineTicketListKey_When_Inspected()
    {
        CacheKeys.TicketList.Should().Be("heimdall:tickets:all");
    }
}

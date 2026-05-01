using FluentAssertions;
using Heimdall.Core.Models.Pagination;

namespace Heimdall.Core.Tests.Models;

public class PagedResultTests
{
    [Fact]
    public void Should_HaveEmptyDefaults_When_NewlyCreated()
    {
        var result = new PagedResult<string>();

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.Page.Should().Be(0);
        result.PageSize.Should().Be(0);
        result.TotalPages.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(99, 25, 4)]
    [InlineData(100, 25, 4)]
    [InlineData(101, 25, 5)]
    public void Should_ComputeTotalPages_When_PageSizeIsPositive(int totalCount, int pageSize, int expectedPages)
    {
        var result = new PagedResult<string>
        {
            Items = new[] { "a" },
            TotalCount = totalCount,
            Page = 1,
            PageSize = pageSize,
        };

        result.TotalPages.Should().Be(expectedPages);
    }

    [Fact]
    public void Should_ReturnZeroTotalPages_When_PageSizeIsZero()
    {
        var result = new PagedResult<string> { TotalCount = 50, PageSize = 0 };
        result.TotalPages.Should().Be(0);
    }
}

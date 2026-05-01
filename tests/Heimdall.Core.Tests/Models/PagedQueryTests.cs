using FluentAssertions;
using Heimdall.Core.Models.Pagination;

namespace Heimdall.Core.Tests.Models;

public class PagedQueryTests
{
    [Fact]
    public void Should_ApplyDefaults_When_DefaultConstructed()
    {
        var query = new PagedQuery();

        query.Page.Should().Be(1);
        query.PageSize.Should().Be(25);
        query.SearchText.Should().BeNull();
        query.SortField.Should().Be("DateCreated");
        query.SortDirection.Should().Be(SortDirection.Descending);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void Should_ClampPageToAtLeastOne_When_Constructed(int input, int expected)
    {
        var query = new PagedQuery(input, 10, null, "Title", SortDirection.Ascending);
        query.Page.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(50, 50)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Should_ClampPageSizeToValidRange_When_Constructed(int input, int expected)
    {
        var query = new PagedQuery(1, input, null, "Title", SortDirection.Ascending);
        query.PageSize.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_NormalizeSearchToNull_When_BlankOrWhitespace(string? input)
    {
        var query = new PagedQuery(1, 10, input, "Title", SortDirection.Ascending);
        query.SearchText.Should().BeNull();
    }

    [Fact]
    public void Should_TrimSearchText_When_PaddedWithWhitespace()
    {
        var query = new PagedQuery(1, 10, "  bug  ", "Title", SortDirection.Ascending);
        query.SearchText.Should().Be("bug");
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("title")]
    [InlineData("DateUpdated")]
    [InlineData("Assignee")]
    public void Should_AcceptKnownSortField_When_Constructed(string sortField)
    {
        var query = new PagedQuery(1, 10, null, sortField, SortDirection.Ascending);
        query.SortField.Should().Be(sortField);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("DROP TABLE")]
    public void Should_FallBackToDateCreated_When_SortFieldUnknown(string sortField)
    {
        var query = new PagedQuery(1, 10, null, sortField, SortDirection.Ascending);
        query.SortField.Should().Be("DateCreated");
    }

    [Fact]
    public void Should_ReturnEquivalentValues_When_Sanitized()
    {
        var query = new PagedQuery(2, 50, "foo", "Title", SortDirection.Ascending);

        var sanitized = query.Sanitized();

        sanitized.Should().NotBeNull();
        sanitized.Page.Should().Be(query.Page);
        sanitized.PageSize.Should().Be(query.PageSize);
        sanitized.SearchText.Should().Be(query.SearchText);
        sanitized.SortField.Should().Be(query.SortField);
        sanitized.SortDirection.Should().Be(query.SortDirection);
    }

    [Fact]
    public void Should_ExposeAllowedSortFields_When_Inspected()
    {
        PagedQuery.AllowedSortFields.Keys.Should().Contain(
            new[] { "Title", "Status", "Priority", "Reporter", "Assignee", "DateCreated", "DateUpdated" });
        PagedQuery.AllowedSortFields["DateCreated"].Should().Be("date_created");
    }
}

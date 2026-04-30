using Bunit;
using FluentAssertions;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;
using Heimdall.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Heimdall.Web.Tests.Components.Pages;

public class TicketsPageTests : BunitContext
{
    private readonly Mock<ITicketService> _service = new(MockBehavior.Loose);
    private readonly List<TicketDto> _store = new();

    public TicketsPageTests()
    {
        Services.AddSingleton(_service.Object);

        _service
            .Setup(s => s.GetPagedAsync(It.IsAny<PagedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PagedQuery q, CancellationToken _) =>
            {
                IEnumerable<TicketDto> items = _store;
                if (!string.IsNullOrEmpty(q.SearchText))
                {
                    items = items.Where(t =>
                        (t.Title ?? string.Empty).Contains(q.SearchText, StringComparison.OrdinalIgnoreCase));
                }

                items = q.SortDirection == SortDirection.Ascending
                    ? items.OrderBy(t => t.Title, StringComparer.Ordinal)
                    : items.OrderByDescending(t => t.Title, StringComparer.Ordinal);

                var total = items.Count();
                var page = items.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToList();

                return new PagedResult<TicketDto>
                {
                    Items = page,
                    TotalCount = total,
                    Page = q.Page,
                    PageSize = q.PageSize,
                };
            });

        _service
            .Setup(s => s.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) =>
            {
                var removed = _store.RemoveAll(t => t.Id == id);
                return removed > 0;
            });
    }

    private void SeedStore(int count, params (TicketStatus, TicketPriority)[] variants)
    {
        for (var i = 0; i < count; i++)
        {
            var v = variants.Length > 0 ? variants[i % variants.Length] : (TicketStatus.Open, TicketPriority.Medium);
            _store.Add(new TicketDto
            {
                Id = i + 1,
                Title = $"Ticket {(char)('A' + i)}",
                Description = "d",
                Status = v.Item1,
                Priority = v.Item2,
                Reporter = $"r{i}",
                Assignee = i % 2 == 0 ? null : $"a{i}",
                DateCreated = DateTimeOffset.UtcNow.AddMinutes(-i),
                DateUpdated = DateTimeOffset.UtcNow,
            });
        }
    }

    [Fact]
    public void Should_RenderEmptyState_When_NoTicketsExist()
    {
        var cut = Render<Tickets>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No tickets yet"));
        cut.Find("a.btn-primary[href='/tickets/new']").Should().NotBeNull();
    }

    [Fact]
    public void Should_RenderTicketRows_When_DataExists()
    {
        SeedStore(3,
            (TicketStatus.Open, TicketPriority.Low),
            (TicketStatus.InProgress, TicketPriority.Medium),
            (TicketStatus.Resolved, TicketPriority.High));

        var cut = Render<Tickets>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Ticket A");
            cut.Markup.Should().Contain("Ticket B");
            cut.Markup.Should().Contain("Ticket C");
        });
        cut.FindAll("tbody tr").Count.Should().Be(3);
    }

    [Fact]
    public void Should_RenderPriorityAndStatusBadgesForAllVariants_When_Rendered()
    {
        SeedStore(8,
            (TicketStatus.Open, TicketPriority.Low),
            (TicketStatus.InProgress, TicketPriority.Medium),
            (TicketStatus.Resolved, TicketPriority.High),
            (TicketStatus.Closed, TicketPriority.Critical));

        var cut = Render<Tickets>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("text-bg-primary");
            cut.Markup.Should().Contain("text-bg-info");
            cut.Markup.Should().Contain("text-bg-success");
            cut.Markup.Should().Contain("text-bg-secondary");
            cut.Markup.Should().Contain("text-bg-warning");
            cut.Markup.Should().Contain("text-bg-danger");
        });
    }

    [Fact]
    public async Task Should_RenderEmptySearchState_When_SearchYieldsNoResultsAndClearReturnsAll()
    {
        SeedStore(2);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket A"));

        var input = cut.Find("input[type='search']");
        await input.InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "zzz" });
        await Task.Delay(400); // allow the 300ms debounce to fire
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No tickets found"));

        // Click "Clear search"
        cut.Find("button.btn-outline-secondary:not([type='button'])").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket A"));
    }

    [Fact]
    public async Task Should_TriggerImmediateSearch_When_EnterKeyPressed()
    {
        SeedStore(2);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket A"));

        var input = cut.Find("input[type='search']");
        await input.InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Ticket A" });
        await input.KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Ticket A");
            cut.Markup.Should().NotContain("Ticket B");
        });
    }

    [Fact]
    public async Task Should_TriggerImmediateSearch_When_SearchButtonClicked()
    {
        SeedStore(2);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket A"));

        var input = cut.Find("input[type='search']");
        await input.InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Ticket B" });
        cut.Find("button[aria-label='Search']").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Ticket A"));
    }

    [Fact]
    public void Should_IgnoreNonEnterKeydown_When_KeyPressed()
    {
        SeedStore(1);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket A"));
        var input = cut.Find("input[type='search']");

        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "a" });

        // Just exercises the early return; no assertion on render needed.
        cut.Markup.Should().Contain("Ticket A");
    }

    [Fact]
    public void Should_ChangePageSize_When_DropdownChanged()
    {
        SeedStore(15);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket"));

        var select = cut.Find("#pageSizeSelect");
        select.Change("10");

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(10));
    }

    [Fact]
    public void Should_ToggleSortDirection_When_SortColumnClickedTwice()
    {
        SeedStore(3);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(3));

        // First click → ascending on Title
        cut.FindAll("th button").First().Click();
        cut.WaitForAssertion(() =>
        {
            var first = cut.FindAll("tbody tr td.fw-medium").First().TextContent;
            first.Should().Be("Ticket A");
        });

        // Second click → descending
        cut.FindAll("th button").First().Click();
        cut.WaitForAssertion(() =>
        {
            var first = cut.FindAll("tbody tr td.fw-medium").First().TextContent;
            first.Should().Be("Ticket C");
        });

        // Switch column → ascending on Status
        cut.FindAll("th button")[1].Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("aria-sort=\"ascending\""));
    }

    [Fact]
    public async Task Should_PaginateAndDelete_When_MultiplePagesPresent()
    {
        SeedStore(20);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().BeGreaterThan(0));

        // Change page size to 10 to get 2 pages
        cut.Find("#pageSizeSelect").Change("10");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(10));

        // Click "Next page"
        cut.Find("button[aria-label='Next page']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("aria-current=\"page\""));

        // Click "Previous page"
        cut.Find("button[aria-label='Previous page']").Click();
        await Task.Delay(50);

        // Click a numbered page (page 2)
        cut.Find("button[aria-label='Page 2']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Showing"));

        // Delete the only row on page 2 (we have 20, page size 10 → 10 rows on page 2).
        // We'll just exercise delete on the first row.
        cut.FindAll("button.btn-outline-danger").First().Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(9));
    }

    [Fact]
    public async Task Should_RenderEllipsisAndManyPages_When_TotalPagesIsLarge()
    {
        SeedStore(80);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().BeGreaterThan(0));

        cut.Find("#pageSizeSelect").Change("10"); // 8 pages → ellipsis path
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(10));

        cut.Markup.Should().Contain("…");

        // Navigate to a middle page via "Page N"
        cut.Find("button[aria-label='Page 4']").Click();
        await Task.Delay(50);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("aria-current=\"page\""));

        // Now jump to last page region.
        cut.Find("button[aria-label='Next page']").Click();
        await Task.Delay(50);
        cut.Find("button[aria-label='Next page']").Click();
        await Task.Delay(50);
        cut.Find("button[aria-label='Next page']").Click();
        await Task.Delay(50);
        cut.Find("button[aria-label='Next page']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("aria-current=\"page\""));
    }

    [Fact]
    public void Should_RecedeToPreviousPage_When_LastRowOnLastPageIsDeleted()
    {
        SeedStore(11);
        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().BeGreaterThan(0));

        cut.Find("#pageSizeSelect").Change("10");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(10));

        cut.Find("button[aria-label='Next page']").Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));

        cut.FindAll("button.btn-outline-danger").First().Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(10));
    }
}

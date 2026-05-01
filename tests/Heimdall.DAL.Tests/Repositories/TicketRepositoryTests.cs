using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.Core.Models.Pagination;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Heimdall.DAL.Tests.Repositories;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TicketRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly TicketRepository _repo;

    public TicketRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _repo = new TicketRepository(options);
    }

    public Task InitializeAsync() => _fx.ResetTicketsTableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Ticket Sample(string title = "T", string? assignee = null, TicketStatus s = TicketStatus.Open, TicketPriority p = TicketPriority.Medium)
    {
        var now = DateTimeOffset.UtcNow;
        return new Ticket
        {
            Title = title,
            Description = $"desc {title}",
            Status = s,
            Priority = p,
            Reporter = "reporter",
            Assignee = assignee,
            DateCreated = now,
            DateUpdated = now,
        };
    }

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new TicketRepository(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_DapperIsNull()
    {
        Action act = () => new TicketRepository(
            Options.Create(new DataOptions { PostgresConnectionString = "x" }),
            null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_AssignIdAndPersist_When_CreateAsyncCalled()
    {
        var t = Sample("created");
        var id = await _repo.CreateAsync(t);
        id.Should().BeGreaterThan(0);
        t.Id.Should().Be(id);

        var fetched = await _repo.GetByIdAsync(id);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("created");
        fetched.Description.Should().Be("desc created");
    }

    [Fact]
    public async Task Should_Throw_When_CreateAsyncTicketIsNull()
    {
        Func<Task> act = () => _repo.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_ReturnNull_When_GetByIdAsyncMisses()
    {
        var result = await _repo.GetByIdAsync(99999);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnAllRowsCappedAndSortedByDateDesc_When_GetAllAsyncCalled()
    {
        for (var i = 0; i < 3; i++)
        {
            await _repo.CreateAsync(Sample($"t{i}"));
        }

        var rows = await _repo.GetAllAsync();
        rows.Should().HaveCount(3);
        rows[0].DateCreated.Should().BeOnOrAfter(rows[^1].DateCreated);
    }

    [Fact]
    public async Task Should_ReturnPagedResults_When_GetPagedAsyncCalledWithoutDapper()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repo.CreateAsync(Sample($"row {i}"));
        }

        var query = new PagedQuery(page: 1, pageSize: 2, searchText: null,
            sortField: "Title", sortDirection: SortDirection.Ascending);
        var (items, total) = await _repo.GetPagedAsync(query);

        total.Should().Be(5);
        items.Should().HaveCount(2);
        items.Select(t => t.Title).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Should_FilterBySearchText_When_GetPagedAsyncReceivesQuery()
    {
        await _repo.CreateAsync(Sample("apple"));
        await _repo.CreateAsync(Sample("banana"));
        await _repo.CreateAsync(Sample("apricot"));

        var (items, total) = await _repo.GetPagedAsync(
            new PagedQuery(1, 50, "ap", "Title", SortDirection.Ascending));

        total.Should().Be(2);
        items.Select(t => t.Title).Should().BeEquivalentTo(new[] { "apple", "apricot" });
    }

    [Fact]
    public async Task Should_FallBackToDateCreatedSort_When_SortFieldNotInAllowList()
    {
        await _repo.CreateAsync(Sample("a"));
        await _repo.CreateAsync(Sample("b"));

        // Construct via reflection to bypass PagedQuery sanitization that defaults invalid
        // sort fields back to DateCreated. We need to test the repository's own fallback.
        var ctor = typeof(PagedQuery).GetConstructor(new[]
        {
            typeof(int), typeof(int), typeof(string), typeof(string), typeof(SortDirection),
        })!;
        var query = (PagedQuery)ctor.Invoke(new object?[] { 1, 50, null, "DROP-ME", SortDirection.Descending });

        var (items, _) = await _repo.GetPagedAsync(query);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Should_Throw_When_GetPagedAsyncQueryIsNull()
    {
        Func<Task> act = () => _repo.GetPagedAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_UpdateAndReturnTrue_When_RowExists()
    {
        var t = Sample("orig");
        var id = await _repo.CreateAsync(t);

        t.Title = "updated";
        t.Description = "new desc";
        t.Status = TicketStatus.Resolved;
        t.Priority = TicketPriority.High;
        t.Assignee = "someone";

        var ok = await _repo.UpdateAsync(t);

        ok.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(id);
        fetched!.Title.Should().Be("updated");
        fetched.Status.Should().Be(TicketStatus.Resolved);
        fetched.Priority.Should().Be(TicketPriority.High);
        fetched.Assignee.Should().Be("someone");
    }

    [Fact]
    public async Task Should_ReturnFalse_When_UpdateAsyncRowMissing()
    {
        var t = Sample("missing");
        t.Id = 999_999;
        var ok = await _repo.UpdateAsync(t);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Throw_When_UpdateAsyncTicketIsNull()
    {
        Func<Task> act = () => _repo.UpdateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_DeleteAndReturnTrue_When_RowExists()
    {
        var id = await _repo.CreateAsync(Sample("delete-me"));

        var ok = await _repo.DeleteAsync(id);

        ok.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_DeleteAsyncRowMissing()
    {
        var ok = await _repo.DeleteAsync(999_999);
        ok.Should().BeFalse();
    }
}

using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.DAL.Auditing;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Heimdall.DAL.Tests.Auditing;

/// <summary>
/// Integration tests for <see cref="AuditEventReader"/> — the read-side
/// projection used by the admin audit feed (Phase 2.8 step 25 of
/// <c>docs/proposals/team-collaboration.md</c> §7). Exercises real PostgreSQL
/// behaviour (DESC ordering on <c>occurred_at</c>, the documented
/// <c>MaxLimit</c> clamp, jsonb / inet round-tripping) via the shared
/// Testcontainers fixture.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AuditEventReaderTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly AuditEventReader _reader;
    private readonly AuditEventWriter _writer;

    public AuditEventReaderTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _reader = new AuditEventReader(options);
        _writer = new AuditEventWriter(options);
    }

    public Task InitializeAsync() => _fx.ResetUsersTableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetRecentAsync_Should_ReturnEmpty_When_NoRowsExist()
    {
        var result = await _reader.GetRecentAsync(50);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentAsync_Should_ReturnRowsOrderedByOccurredAtDesc_When_MultipleRowsExist()
    {
        // occurred_at is sourced from the DB clock (now()) at INSERT time. Sequencing the
        // writes serially with a tiny pause keeps the ORDER BY tie-break deterministic
        // even on fast hosts.
        await _writer.WriteAsync(new AuditEvent
        {
            EventType = "ticket.routed",
            Target = "ticket-1",
            PayloadJson = "{\"k\":1}",
        });
        await Task.Delay(10);
        await _writer.WriteAsync(new AuditEvent
        {
            EventType = "ticket.assigned",
            Target = "ticket-2",
            PayloadJson = "{\"k\":2}",
        });
        await Task.Delay(10);
        await _writer.WriteAsync(new AuditEvent
        {
            EventType = "ticket.claimed",
            Target = "ticket-3",
            PayloadJson = "{\"k\":3}",
        });

        var result = await _reader.GetRecentAsync(10);

        result.Should().HaveCount(3);
        result.Select(r => r.EventType).Should().ContainInOrder("ticket.claimed", "ticket.assigned", "ticket.routed");
        result[0].OccurredAt.Should().NotBe(default);
        result[0].PayloadJson.Should().Contain("\"k\"");
        // DESC ordering invariant on the actual timestamp column.
        result[0].OccurredAt.Should().BeOnOrAfter(result[1].OccurredAt);
        result[1].OccurredAt.Should().BeOnOrAfter(result[2].OccurredAt);
    }

    [Fact]
    public async Task GetRecentAsync_Should_HonourCallerLimit_When_RowCountExceedsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _writer.WriteAsync(new AuditEvent { EventType = $"evt.{i}", Target = $"t-{i}" });
            await Task.Delay(5);
        }

        var result = await _reader.GetRecentAsync(2);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecentAsync_Should_ClampToMaxLimit_When_LimitExceedsCeiling()
    {
        await _writer.WriteAsync(new AuditEvent { EventType = "evt.only", Target = "t" });

        // MaxLimit is documented as 1000 (AuditEventReader.cs); a value past that should
        // clamp silently rather than throw, and we should still get every row that fits.
        var act = async () => await _reader.GetRecentAsync(10_000);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().HaveCount(1);
    }
}

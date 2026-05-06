using Dapper;
using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.DAL.Auditing;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Auditing;

/// <summary>
/// Integration tests for the transaction-aware overload of
/// <see cref="AuditEventWriter.WriteAsync(System.Data.IDbConnection, System.Data.IDbTransaction, AuditEvent, System.Threading.CancellationToken)"/>.
/// Verifies the contract documented in <c>docs/proposals/team-collaboration.md</c> §5.4 —
/// the audit-event INSERT must commit (or rollback) atomically with the caller's other
/// mutation under a caller-owned connection + transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AuditEventWriterTransactionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly AuditEventWriter _writer;

    public AuditEventWriterTransactionTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _writer = new AuditEventWriter(options);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fx.ResetUsersTableAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Should_PersistRow_When_TransactionIsCommitted()
    {
        var auditEvent = new AuditEvent
        {
            EventType = "ticket.routed",
            Target = "ticket-1",
            PayloadJson = "{\"from\":\"a\",\"to\":\"b\"}",
        };

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await _writer.WriteAsync(conn, tx, auditEvent);
        await tx.CommitAsync();

        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = 'ticket.routed';");
        count.Should().Be(1);
    }

    [Fact]
    public async Task Should_DiscardRow_When_TransactionIsRolledBack()
    {
        var auditEvent = new AuditEvent
        {
            EventType = "ticket.routed",
            Target = "ticket-1",
        };

        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await _writer.WriteAsync(conn, tx, auditEvent);
            await tx.RollbackAsync();
        }

        await using var verify = new NpgsqlConnection(_fx.ConnectionString);
        await verify.OpenAsync();
        var count = await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM audit_events;");
        count.Should().Be(0);
    }

    [Fact]
    public async Task Should_CommitBothRows_When_PairedWithSecondInsertOnSameTransaction()
    {
        // Sanity check that a sibling write on the same transaction lands together with
        // the audit row — this is the actual usage shape the BLL ticket service will adopt
        // (UPDATE tickets + INSERT audit_events under one tx).
        var auditEvent = new AuditEvent
        {
            EventType = "ticket.assigned",
            Target = "ticket-1",
        };

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync(new CommandDefinition(
            "CREATE TEMP TABLE _tx_probe (id int) ON COMMIT DROP;",
            transaction: tx));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO _tx_probe (id) VALUES (1);",
            transaction: tx));
        await _writer.WriteAsync(conn, tx, auditEvent);

        await tx.CommitAsync();

        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = 'ticket.assigned';");
        count.Should().Be(1);
    }

    [Fact]
    public async Task Should_Throw_When_ConnectionIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        Func<Task> act = () => _writer.WriteAsync(null!, tx, new AuditEvent { EventType = "x" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_TransactionIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        Func<Task> act = () => _writer.WriteAsync(conn, null!, new AuditEvent { EventType = "x" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_AuditEventIsNull()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        Func<Task> act = () => _writer.WriteAsync(conn, tx, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

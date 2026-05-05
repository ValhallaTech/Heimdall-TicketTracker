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
/// Integration tests for <see cref="AuditEventWriter"/>. Each test runs against a real
/// Postgres container provided by <see cref="PostgresFixture"/>, with the
/// <c>users</c>/<c>audit_events</c> tables reset before each test for determinism.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AuditEventWriterTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly AuditEventWriter _writer;

    public AuditEventWriterTests(PostgresFixture fx)
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
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new AuditEventWriter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_PersistRow_When_WriteAsyncCalled()
    {
        var auditEvent = new AuditEvent
        {
            ActorUserId = null,
            EventType = "login.failure.unknown_user",
            Target = "target-1",
            Ip = "192.0.2.1",
            UserAgent = "Mozilla/5.0",
            PayloadJson = "{\"k\":\"v\"}",
        };

        await _writer.WriteAsync(auditEvent);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync<AuditRow>(
            "SELECT event_type AS EventType, target AS Target, host(ip) AS Ip, user_agent AS UserAgent, payload::text AS Payload FROM audit_events");

        row.EventType.Should().Be("login.failure.unknown_user");
        row.Target.Should().Be("target-1");
        row.Ip.Should().Be("192.0.2.1");
        row.UserAgent.Should().Be("Mozilla/5.0");
        row.Payload.Should().Contain("\"k\"").And.Contain("\"v\"");
    }

    [Fact]
    public async Task Should_PersistNullIp_When_IpIsNull()
    {
        var auditEvent = new AuditEvent
        {
            EventType = "login.success",
            Ip = null,
        };

        await _writer.WriteAsync(auditEvent);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        bool ipIsNull = await conn.QuerySingleAsync<bool>("SELECT ip IS NULL FROM audit_events");
        ipIsNull.Should().BeTrue();
    }

    [Fact]
    public async Task Should_PersistNullActorUserId_When_ActorIsAnonymous()
    {
        var auditEvent = new AuditEvent
        {
            ActorUserId = null,
            EventType = "login.failure.unknown_user",
            Ip = "192.0.2.1",
        };

        await _writer.WriteAsync(auditEvent);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        bool actorIsNull = await conn.QuerySingleAsync<bool>("SELECT actor_user_id IS NULL FROM audit_events");
        actorIsNull.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Throw_When_AuditEventIsNull()
    {
        Func<Task> act = () => _writer.WriteAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_HonourCancellation_When_TokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var auditEvent = new AuditEvent { EventType = "login.success" };

        Func<Task> act = () => _writer.WriteAsync(auditEvent, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class AuditRow
    {
        public string EventType { get; set; } = string.Empty;
        public string? Target { get; set; }
        public string? Ip { get; set; }
        public string? UserAgent { get; set; }
        public string Payload { get; set; } = string.Empty;
    }
}

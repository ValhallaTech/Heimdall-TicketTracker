using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Repositories;

/// <summary>
/// Integration tests for <see cref="RefreshTokenRepository"/>. Backed by the shared
/// <see cref="PostgresFixture"/> Testcontainers Postgres so the
/// <c>M202605200001_CreateRefreshTokens</c> schema (CHECK constraint on
/// <c>revoked_reason</c>, partial index, FK to <c>users</c>, FKs back to
/// <c>refresh_tokens(id)</c>) is exercised against a real Postgres instance.
/// Unlike <see cref="SigningKeyRepositoryTests"/>, refresh_tokens is not in the
/// hardened two-role posture, so these tests run as the migration-runner role
/// (the default connection string) — no <c>SET ROLE heimdall_app</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RefreshTokenRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly RefreshTokenRepository _repo;
    private Guid _userId;

    public RefreshTokenRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;
        _repo = new RefreshTokenRepository(Options.Create(new DataOptions
        {
            PostgresConnectionString = fx.ConnectionString,
        }));
    }

    public Task InitializeAsync() => ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InsertAsync_round_trips_via_get_by_hash()
    {
        var inserted = NewToken(_userId);

        await _repo.InsertAsync(inserted);

        var fetched = await _repo.GetByHashAsync(inserted.TokenHash);

        fetched.Should().NotBeNull();
        fetched!.Should().BeEquivalentTo(inserted, opts => opts
            .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(1)))
            .WhenTypeIs<DateTime>());
        fetched.IssuedAt.Kind.Should().Be(DateTimeKind.Utc);
        fetched.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task GetByHashAsync_returns_null_for_unknown_hash()
    {
        var result = await _repo.GetByHashAsync("hash-" + Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_marks_old_revoked_and_inserts_new_in_one_transaction()
    {
        var root = NewToken(_userId);
        await _repo.InsertAsync(root);

        var successor = NewToken(_userId, familyId: root.FamilyId, parentId: root.Id);

        var rotated = await _repo.RotateAsync(root.Id, successor);

        rotated.Should().BeTrue();

        var rootRow = await ReadRawRowAsync(root.Id);
        rootRow.RevokedAt.Should().NotBeNull();
        rootRow.RevokedReason.Should().Be(RefreshTokenRevokedReason.Rotated);
        rootRow.ReplacedBy.Should().Be(successor.Id);

        var successorRow = await ReadRawRowAsync(successor.Id);
        successorRow.RevokedAt.Should().BeNull();
        successorRow.RevokedReason.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_returns_false_and_rolls_back_when_old_already_revoked()
    {
        var root = NewToken(_userId);
        await _repo.InsertAsync(root);

        // Manually revoke as 'logout' so we can prove RotateAsync does not overwrite
        // the existing revoked_reason and does not insert the successor.
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'logout' WHERE id = @Id;",
                new { Id = root.Id });
        }

        var successor = NewToken(_userId, familyId: root.FamilyId, parentId: root.Id);

        var rotated = await _repo.RotateAsync(root.Id, successor);

        rotated.Should().BeFalse();

        // Successor was rolled back — it must not be visible by token_hash.
        var successorFetched = await _repo.GetByHashAsync(successor.TokenHash);
        successorFetched.Should().BeNull();

        // Root row still bears the 'logout' reason, not 'rotated'.
        var rootRow = await ReadRawRowAsync(root.Id);
        rootRow.RevokedReason.Should().Be(RefreshTokenRevokedReason.Logout);
        rootRow.ReplacedBy.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_race_exactly_one_winner()
    {
        var root = NewToken(_userId);
        await _repo.InsertAsync(root);

        var successorA = NewToken(_userId, familyId: root.FamilyId, parentId: root.Id);
        var successorB = NewToken(_userId, familyId: root.FamilyId, parentId: root.Id);

        var taskA = Task.Run(() => _repo.RotateAsync(root.Id, successorA));
        var taskB = Task.Run(() => _repo.RotateAsync(root.Id, successorB));

        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r).Should().Be(1, "exactly one rotation must win the race");
        results.Count(r => !r).Should().Be(1, "exactly one rotation must lose the race");

        bool aWon = results[0];
        var winner = aWon ? successorA : successorB;
        var loser = aWon ? successorB : successorA;

        // Winner's row is present and active.
        var winnerFetched = await _repo.GetByHashAsync(winner.TokenHash);
        winnerFetched.Should().NotBeNull();
        winnerFetched!.RevokedAt.Should().BeNull();

        // Loser's row was rolled back and never persisted.
        var loserFetched = await _repo.GetByHashAsync(loser.TokenHash);
        loserFetched.Should().BeNull();

        // Root's replaced_by points at the winner.
        var rootRow = await ReadRawRowAsync(root.Id);
        rootRow.RevokedReason.Should().Be(RefreshTokenRevokedReason.Rotated);
        rootRow.ReplacedBy.Should().Be(winner.Id);
    }

    [Fact]
    public async Task RevokeFamilyAsync_revokes_every_active_member()
    {
        var familyId = Guid.NewGuid();
        var first = NewToken(_userId, familyId: familyId);
        var second = NewToken(_userId, familyId: familyId);
        var third = NewToken(_userId, familyId: familyId);

        await _repo.InsertAsync(first);
        await _repo.InsertAsync(second);
        await _repo.InsertAsync(third);

        // Pre-revoke `second` with 'logout' so the WHERE revoked_at IS NULL
        // guard must skip it.
        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'logout' WHERE id = @Id;",
                new { Id = second.Id });
        }

        var affected = await _repo.RevokeFamilyAsync(familyId, RefreshTokenRevokedReason.FamilyReplay);

        affected.Should().Be(2);

        var firstRow = await ReadRawRowAsync(first.Id);
        var secondRow = await ReadRawRowAsync(second.Id);
        var thirdRow = await ReadRawRowAsync(third.Id);

        firstRow.RevokedReason.Should().Be(RefreshTokenRevokedReason.FamilyReplay);
        firstRow.RevokedAt.Should().NotBeNull();
        thirdRow.RevokedReason.Should().Be(RefreshTokenRevokedReason.FamilyReplay);
        thirdRow.RevokedAt.Should().NotBeNull();

        // Pre-revoked row is untouched.
        secondRow.RevokedReason.Should().Be(RefreshTokenRevokedReason.Logout);
    }

    [Fact]
    public async Task RevokeFamilyAsync_rejects_unknown_reason()
    {
        var familyId = Guid.NewGuid();
        var first = NewToken(_userId, familyId: familyId);
        var second = NewToken(_userId, familyId: familyId);
        await _repo.InsertAsync(first);
        await _repo.InsertAsync(second);

        Func<Task> act = () => _repo.RevokeFamilyAsync(familyId, "banana");

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // No row should have been touched — the allow-list check happens before
        // any DB call.
        var firstRow = await ReadRawRowAsync(first.Id);
        var secondRow = await ReadRawRowAsync(second.Id);
        firstRow.RevokedAt.Should().BeNull();
        firstRow.RevokedReason.Should().BeNull();
        secondRow.RevokedAt.Should().BeNull();
        secondRow.RevokedReason.Should().BeNull();
    }

    [Fact]
    public async Task Family_revoke_does_not_affect_other_families()
    {
        var familyA = Guid.NewGuid();
        var familyB = Guid.NewGuid();

        var a1 = NewToken(_userId, familyId: familyA);
        var a2 = NewToken(_userId, familyId: familyA);
        var b1 = NewToken(_userId, familyId: familyB);
        var b2 = NewToken(_userId, familyId: familyB);

        await _repo.InsertAsync(a1);
        await _repo.InsertAsync(a2);
        await _repo.InsertAsync(b1);
        await _repo.InsertAsync(b2);

        var affected = await _repo.RevokeFamilyAsync(familyA, RefreshTokenRevokedReason.FamilyReplay);

        affected.Should().Be(2);

        (await ReadRawRowAsync(a1.Id)).RevokedReason.Should().Be(RefreshTokenRevokedReason.FamilyReplay);
        (await ReadRawRowAsync(a2.Id)).RevokedReason.Should().Be(RefreshTokenRevokedReason.FamilyReplay);

        var b1Row = await ReadRawRowAsync(b1.Id);
        var b2Row = await ReadRawRowAsync(b2.Id);
        b1Row.RevokedAt.Should().BeNull();
        b1Row.RevokedReason.Should().BeNull();
        b2Row.RevokedAt.Should().BeNull();
        b2Row.RevokedReason.Should().BeNull();
    }

    private static RefreshToken NewToken(
        Guid userId,
        Guid? familyId = null,
        Guid? parentId = null)
    {
        var now = DateTime.UtcNow;
        return new RefreshToken(
            Id: Guid.NewGuid(),
            UserId: userId,
            TokenHash: "hash-" + Guid.NewGuid().ToString("N"),
            FamilyId: familyId ?? Guid.NewGuid(),
            ParentId: parentId,
            ReplacedBy: null,
            IssuedAt: DateTime.SpecifyKind(now, DateTimeKind.Utc),
            ExpiresAt: DateTime.SpecifyKind(now + TimeSpan.FromDays(30), DateTimeKind.Utc),
            RevokedAt: null,
            RevokedReason: null);
    }

    private async Task<RawRefreshTokenRow> ReadRawRowAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<RawRefreshTokenRow>(
            "SELECT id AS Id, user_id AS UserId, token_hash AS TokenHash, "
            + "family_id AS FamilyId, parent_id AS ParentId, replaced_by AS ReplacedBy, "
            + "issued_at AS IssuedAt, expires_at AS ExpiresAt, "
            + "revoked_at AS RevokedAt, revoked_reason AS RevokedReason "
            + "FROM refresh_tokens WHERE id = @Id;",
            new { Id = id });
    }

    private async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        // refresh_tokens has FKs back to itself plus a FK to users(id) ON DELETE
        // CASCADE — TRUNCATE ... CASCADE is the simplest reset that handles both.
        await conn.ExecuteAsync("TRUNCATE refresh_tokens RESTART IDENTITY CASCADE;");

        // Other repo tests in the PostgresCollection wipe `users` in their own
        // reset hooks, so we cannot rely on a previously-seeded user surviving
        // between tests. Insert a fresh row with a unique email and capture the
        // generated id for use as the FK target for every refresh_tokens row
        // this test class inserts.
        var unique = Guid.NewGuid().ToString("N");
        _userId = await conn.QuerySingleAsync<Guid>(
            @"INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
              VALUES (@Email, @NormalizedEmail, 's', 'c', now(), now())
              RETURNING id;",
            new
            {
                Email = $"rt-{unique}@example.com",
                NormalizedEmail = $"RT-{unique}@EXAMPLE.COM",
            });
    }

    /// <summary>
    /// Materialisation DTO used by direct SELECTs in the test body so we can assert
    /// on the persisted values without going through <see cref="RefreshTokenRepository"/>
    /// (whose code path is itself under test).
    /// </summary>
    private sealed class RawRefreshTokenRow
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }

        public string TokenHash { get; set; } = string.Empty;

        public Guid FamilyId { get; set; }

        public Guid? ParentId { get; set; }

        public Guid? ReplacedBy { get; set; }

        public DateTime IssuedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? RevokedAt { get; set; }

        public string? RevokedReason { get; set; }
    }
}

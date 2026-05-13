using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
/// Integration tests for <see cref="SigningKeyRepository"/>. Backed by the shared
/// <see cref="PostgresFixture"/> Testcontainers Postgres so the SECURITY DEFINER
/// functions, column-level grants, Row Level Security policies, and audit trigger
/// installed by <see cref="Heimdall.DAL.Migrations.M202605130001_CreateSigningKeys"/>
/// are all exercised against a real Postgres instance.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SigningKeyRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly SigningKeyRepository _repo;

    public SigningKeyRepositoryTests(PostgresFixture fx)
    {
        _fx = fx;

        // SET ROLE heimdall_app — exercises the SECURITY DEFINER + column-grant boundary
        // for every command this repository instance issues. The Npgsql `Options`
        // parameter is forwarded to libpq's startup packet; `-c role=...` sets the
        // session role on connect, equivalent to executing `SET ROLE heimdall_app;`
        // immediately after `BEGIN`. Single-quoted because the value contains `=`.
        _repo = new SigningKeyRepository(Options.Create(new DataOptions
        {
            PostgresConnectionString = AppRoleConnectionString(fx.ConnectionString),
        }));
    }

    private static string AppRoleConnectionString(string baseCs) =>
        baseCs + ";Options='-c role=heimdall_app'";

    public Task InitializeAsync() => ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InsertAsync_round_trips_via_security_definer_function()
    {
        var (kid, alg, jwkJson, ciphertext, notBefore, notAfter) = NewKeyArgs();

        await _repo.InsertAsync(kid, alg, jwkJson, ciphertext, notBefore, notAfter);

        // Read back as superuser so we can see private_key_protected directly.
        await using var conn = NewSuperConnection();
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync<(string Kid, string Alg, string Jwk, byte[] Bytes, DateTime Nb, DateTime Na)>(
            "SELECT kid, alg, public_jwk::text, private_key_protected, not_before, not_after FROM signing_keys WHERE kid=@Kid;",
            new { Kid = kid });

        row.Kid.Should().Be(kid);
        row.Alg.Should().Be(alg);
        JsonDocument.Parse(row.Jwk).RootElement.GetProperty("kty").GetString().Should().Be("RSA");
        row.Bytes.Should().BeEquivalentTo(ciphertext);
        row.Nb.ToUniversalTime().Should().BeCloseTo(notBefore, TimeSpan.FromSeconds(1));
        row.Na.ToUniversalTime().Should().BeCloseTo(notAfter, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReadPrivateKeyProtectedAsync_returns_bytes_via_security_definer()
    {
        var (kid, alg, jwkJson, ciphertext, notBefore, notAfter) = NewKeyArgs();
        await _repo.InsertAsync(kid, alg, jwkJson, ciphertext, notBefore, notAfter);

        byte[]? read = await _repo.ReadPrivateKeyProtectedAsync(kid);

        read.Should().NotBeNull();
        read!.Should().BeEquivalentTo(ciphertext);
    }

    [Fact]
    public async Task ReadPrivateKeyProtectedAsync_returns_null_for_unknown_kid()
    {
        byte[]? read = await _repo.ReadPrivateKeyProtectedAsync("does-not-exist");

        read.Should().BeNull();
    }

    [Fact]
    public async Task heimdall_app_role_cannot_select_private_key_protected_directly()
    {
        var (kid, alg, jwkJson, ciphertext, notBefore, notAfter) = NewKeyArgs();
        await _repo.InsertAsync(kid, alg, jwkJson, ciphertext, notBefore, notAfter);

        // Connect with SET ROLE heimdall_app to prove the column-level GRANT excludes
        // private_key_protected from the application role.
        await using var conn = new NpgsqlConnection(AppRoleConnectionString(_fx.ConnectionString));
        await conn.OpenAsync();

        Func<Task> act = () => conn.ExecuteAsync("SELECT private_key_protected FROM signing_keys WHERE kid=@Kid;", new { Kid = kid });

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("42501"); // insufficient_privilege
    }

    [Fact]
    public async Task GetCurrentAsync_returns_newest_unretired_in_validity_window()
    {
        DateTime now = DateTime.UtcNow;

        var (oldKid, _, _, _, _, _) = NewKeyArgs();
        await _repo.InsertAsync(oldKid, "RS256", JwkFor(oldKid, "RS256"), new byte[] { 1 }, now - TimeSpan.FromHours(2), now + TimeSpan.FromDays(89));

        var (newerKid, _, _, _, _, _) = NewKeyArgs();
        await _repo.InsertAsync(newerKid, "RS256", JwkFor(newerKid, "RS256"), new byte[] { 2 }, now - TimeSpan.FromMinutes(5), now + TimeSpan.FromDays(90));

        // Retire the older row.
        await _repo.UpdateRetiredAtAsync(oldKid, now);

        var current = await _repo.GetCurrentAsync(now);

        current.Should().NotBeNull();
        current!.Kid.Should().Be(newerKid);
    }

    [Fact]
    public async Task GetTrustedAsync_excludes_retired_and_expired()
    {
        DateTime now = DateTime.UtcNow;

        var currentKid = Guid.NewGuid().ToString("N");
        var retiredKid = Guid.NewGuid().ToString("N");
        var expiredKid = Guid.NewGuid().ToString("N");
        var futureKid = Guid.NewGuid().ToString("N");

        await _repo.InsertAsync(currentKid, "RS256", JwkFor(currentKid, "RS256"), new byte[] { 1 }, now - TimeSpan.FromHours(1), now + TimeSpan.FromDays(30));
        await _repo.InsertAsync(retiredKid, "RS256", JwkFor(retiredKid, "RS256"), new byte[] { 2 }, now - TimeSpan.FromHours(2), now + TimeSpan.FromDays(60));
        await _repo.InsertAsync(expiredKid, "RS256", JwkFor(expiredKid, "RS256"), new byte[] { 3 }, now - TimeSpan.FromDays(10), now - TimeSpan.FromHours(1));
        await _repo.InsertAsync(futureKid, "RS256", JwkFor(futureKid, "RS256"), new byte[] { 4 }, now + TimeSpan.FromMinutes(1), now + TimeSpan.FromDays(90));

        await _repo.UpdateRetiredAtAsync(retiredKid, now);

        var trusted = await _repo.GetTrustedAsync(now);

        trusted.Select(r => r.Kid).Should().BeEquivalentTo(new[] { futureKid, currentKid });

        // Ordered by not_after DESC — futureKid (now+90d) comes before currentKid (now+30d).
        trusted.Select(r => r.Kid).Should().ContainInOrder(futureKid, currentKid);
    }

    [Fact]
    public async Task UpdateRetiredAtAsync_is_idempotent_for_already_retired_row()
    {
        var (kid, alg, jwkJson, ct, nb, na) = NewKeyArgs();
        await _repo.InsertAsync(kid, alg, jwkJson, ct, nb, na);

        DateTime first = DateTime.UtcNow;
        int firstAffected = await _repo.UpdateRetiredAtAsync(kid, first);
        int secondAffected = await _repo.UpdateRetiredAtAsync(kid, first + TimeSpan.FromHours(1));

        firstAffected.Should().Be(1);
        secondAffected.Should().Be(0);

        await using var conn = NewSuperConnection();
        await conn.OpenAsync();
        DateTime persistedRetiredAt = await conn.QuerySingleAsync<DateTime>(
            "SELECT retired_at FROM signing_keys WHERE kid=@Kid;", new { Kid = kid });
        persistedRetiredAt.ToUniversalTime().Should().BeCloseTo(first, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Audit_trigger_writes_one_audit_event_row_per_insert()
    {
        var (kid, alg, jwkJson, ct, nb, na) = NewKeyArgs();

        await _repo.InsertAsync(kid, alg, jwkJson, ct, nb, na);

        await using var conn = NewSuperConnection();
        await conn.OpenAsync();
        var rows = (await conn.QueryAsync<(string EventType, string PayloadKid)>(
            @"SELECT event_type, payload->>'kid' AS payload_kid
              FROM audit_events
              WHERE event_type = 'token.signing_key.generated' AND target = @Kid;",
            new { Kid = kid })).ToList();

        rows.Should().HaveCount(1);
        rows[0].EventType.Should().Be("token.signing_key.generated");
        rows[0].PayloadKid.Should().Be(kid);
    }

    private (string Kid, string Alg, string JwkJson, byte[] Ciphertext, DateTime NotBefore, DateTime NotAfter) NewKeyArgs()
    {
        DateTime now = DateTime.UtcNow;
        string kid = Guid.NewGuid().ToString("N");
        return (
            Kid: kid,
            Alg: "RS256",
            JwkJson: "{\"kty\":\"RSA\",\"kid\":\"" + kid + "\",\"alg\":\"RS256\",\"use\":\"sig\",\"n\":\"abc\",\"e\":\"AQAB\"}",
            Ciphertext: System.Text.Encoding.UTF8.GetBytes("not-actually-encrypted-" + kid),
            NotBefore: now - TimeSpan.FromMinutes(1),
            NotAfter: now + TimeSpan.FromDays(90));
    }

    private NpgsqlConnection NewSuperConnection() => new(_fx.ConnectionString);

    private static string JwkFor(string kid, string alg) =>
        "{\"kty\":\"RSA\",\"kid\":\""
        + kid
        + "\",\"alg\":\""
        + alg
        + "\",\"use\":\"sig\",\"n\":\"abc\",\"e\":\"AQAB\"}";

    private async Task ResetAsync()
    {
        await using var conn = NewSuperConnection();
        await conn.OpenAsync();

        // Per-test cleanup. The Phase-5 migration
        // (M202605130001_CreateSigningKeys) now owns the table-ownership
        // transfer, the un-FORCE of RLS, and the audit_events INSERT
        // grants for both heimdall_signer and heimdall_app — those used
        // to live here as a test-only fixup but are required for the
        // production SECURITY DEFINER write path, so they belong in the
        // migration itself.
        await conn.ExecuteAsync(
            "DELETE FROM signing_keys; DELETE FROM audit_events;");
    }
}

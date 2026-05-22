using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Heimdall.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Phase 5.4 steps 9–10 — integration tests for the JSON-bodied
/// <see cref="ApiAuthEndpoints"/> token endpoints, driven through a real
/// <see cref="WebApplicationFactory{TEntryPoint}"/> against a Testcontainers
/// Postgres so the refresh-rotation transaction, the family-replay sweeper, and
/// the audit-events writes are all exercised end-to-end. Each test seeds its own
/// user (distinct email) so the <c>(ip|email)</c> login rate limiter never bleeds
/// between cases.
/// </summary>
/// <remarks>
/// <para>
/// Each test re-seeds the signing key only once per fixture instance (the first
/// test that needs it). The Phase 5.4 hardening surface deliberately does NOT
/// bootstrap a signing key at startup — operators provision the first key via
/// the runbook — so the test layer is responsible for that seam.
/// </para>
/// <para>
/// The production cookie is <c>Secure=true</c> unconditionally (<c>__Host-</c>
/// prefix), but the in-process <see cref="WebApplicationFactory{TEntryPoint}"/>
/// uses an HTTP scheme. <see cref="HttpClient"/>'s built-in
/// <see cref="System.Net.CookieContainer"/> therefore drops the cookie on
/// auto-attach, so each test reads <c>Set-Cookie</c> off the response and
/// re-attaches the cookie manually on the follow-up request.
/// </para>
/// </remarks>
[Collection("Phase5ApiAuth")]
public sealed class ApiAuthEndpointsTests : IClassFixture<HeimdallWebApplicationFactory>
{
    private const string CookieName = "__Host-heimdall_refresh";

    private readonly HeimdallWebApplicationFactory _factory;

    public ApiAuthEndpointsTests(HeimdallWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------------------
    // POST /api/v1/auth/token
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostToken_ValidNonMfaUser_Returns200_WithAccessAndRefreshCookie()
    {
        // Arrange
        await EnsureSigningKeyAsync();
        const string password = "Acceptance!Token99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-token-success-{Guid.NewGuid():N}@example.com",
            password: password,
            mfaEnabled: false);

        using HttpClient client = CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email = user.Email, password = password });

        // Assert — response shape.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.RootElement.GetProperty("expires_in").GetInt32().Should().BeGreaterThan(0);

        // Refresh cookie attributes (Set-Cookie attribute names are
        // case-insensitive per RFC 6265 — ASP.NET emits them with the canonical
        // mixed case (HttpOnly, Secure, SameSite, Path), so we lower-case the
        // header for attribute assertions while preserving the value-extraction
        // path (which is case-sensitive — see ExtractCookieValue).
        string setCookie = ExtractSetCookie(response, CookieName);
        string setCookieLower = setCookie.ToLowerInvariant();
        setCookieLower.Should().Contain("httponly", because: "HttpOnly is required for the refresh cookie");
        setCookieLower.Should().Contain("secure", because: "__Host- prefix mandates Secure=true");
        setCookieLower.Should().Contain("samesite=strict", because: "cross-site flows must never attach the refresh cookie");
        setCookieLower.Should().Contain("path=/api/v1/auth", because: "the cookie is scoped to the auth subtree only");

        // A single active refresh-tokens row for this user.
        await using var connection = OpenConnection();
        int activeRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = user.Id });
        activeRows.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostToken_MfaEnabledUser_Returns401_WithRequiresTwoFactor()
    {
        // Arrange
        await EnsureSigningKeyAsync();
        const string password = "Acceptance!Mfa99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-token-mfa-{Guid.NewGuid():N}@example.com",
            password: password,
            mfaEnabled: true);

        using HttpClient client = CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email = user.Email, password = password });

        // Assert — per phase-5-checklist.md step 9 the response MUST be
        // 401 + {"requires_two_factor": true}. The current implementation in
        // ApiAuthEndpoints.HandleIssueTokenAsync returns 400 invalid_grant on
        // result.RequiresTwoFactor instead. Skipping until production code is
        // corrected.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("requires_two_factor").GetBoolean().Should().BeTrue();

        // No refresh row should have been inserted.
        await using var connection = OpenConnection();
        int rows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @UserId",
            new { UserId = user.Id });
        rows.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostToken_MfaEnabledUser_DoesNotInsertRefreshRow()
    {
        // Companion to the skipped spec-asserting test above: regardless of the
        // status-code discrepancy, the no-refresh-token-row guarantee MUST hold —
        // production never issues a refresh token to an MFA-enrolled user via
        // the password-grant endpoint.
        await EnsureSigningKeyAsync();
        const string password = "Acceptance!Mfa99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-token-mfa-norow-{Guid.NewGuid():N}@example.com",
            password: password,
            mfaEnabled: true);

        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email = user.Email, password = password });

        response.IsSuccessStatusCode.Should().BeFalse();

        await using var connection = OpenConnection();
        int rows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @UserId",
            new { UserId = user.Id });
        rows.Should().Be(0, because: "the MFA path must never mint a refresh token");

        // No refresh cookie should have been set either.
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies);
        (setCookies ?? Array.Empty<string>())
            .Should().NotContain(c => c.StartsWith(CookieName, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostToken_InvalidCredentials_NoRefreshCookie()
    {
        // The current production path returns 400 invalid_grant for bad password
        // (per ApiAuthEndpoints.InvalidGrant) — the key invariant this test pins
        // is "no refresh cookie on failure", which is the part the spec is
        // unambiguous about.
        await EnsureSigningKeyAsync();
        const string password = "Acceptance!Bad99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-token-badpw-{Guid.NewGuid():N}@example.com",
            password: password,
            mfaEnabled: false);

        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email = user.Email, password = "wrong-password" });

        response.IsSuccessStatusCode.Should().BeFalse();
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies);
        (setCookies ?? Array.Empty<string>())
            .Should().NotContain(c => c.StartsWith(CookieName, StringComparison.Ordinal),
                because: "failed credential exchange must not mint a refresh cookie");
    }

    // -----------------------------------------------------------------------------
    // POST /api/v1/auth/refresh
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRefresh_HappyPath_RotatesAndReturnsNewTokens()
    {
        // Arrange — seed a user + obtain the family-root refresh cookie.
        await EnsureSigningKeyAsync();
        const string password = "Acceptance!Rotate99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-refresh-rotate-{Guid.NewGuid():N}@example.com",
            password: password,
            mfaEnabled: false);

        using HttpClient client = CreateClient();
        (string originalAccessToken, string originalRefreshValue) = await IssueTokensAsync(client, user.Email, password);

        // Capture the original row id before rotating so we can assert linkage.
        Guid originalRowId = await GetSingleActiveRefreshTokenIdAsync(user.Id);

        // Act — call /refresh with the cookie attached manually.
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"{CookieName}={originalRefreshValue}");
        using HttpResponseMessage refreshResponse = await client.SendAsync(refreshRequest);

        // Assert — new access token issued.
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
        string newAccessToken = body.RootElement.GetProperty("access_token").GetString()!;
        newAccessToken.Should().NotBeNullOrWhiteSpace();
        newAccessToken.Should().NotBe(originalAccessToken);

        // A fresh cookie value is set, and is distinct from the original.
        string newSetCookie = ExtractSetCookie(refreshResponse, CookieName);
        string newRefreshValue = ExtractCookieValue(newSetCookie, CookieName);
        newRefreshValue.Should().NotBe(originalRefreshValue);

        // Old row revoked with reason=rotated and replaced_by populated.
        await using var connection = OpenConnection();
        var oldRow = await connection.QuerySingleAsync<(DateTime? revoked_at, string? revoked_reason, Guid? replaced_by)>(
            "SELECT revoked_at, revoked_reason, replaced_by FROM refresh_tokens WHERE id = @Id",
            new { Id = originalRowId });
        oldRow.revoked_at.Should().NotBeNull();
        oldRow.revoked_reason.Should().Be(RefreshTokenRevokedReason.Rotated);
        oldRow.replaced_by.Should().NotBeNull();

        // New row exists, ParentId = old row, active.
        var newRow = await connection.QuerySingleAsync<(Guid id, Guid? parent_id, DateTime? revoked_at)>(
            "SELECT id, parent_id, revoked_at FROM refresh_tokens WHERE id = @Id",
            new { Id = oldRow.replaced_by!.Value });
        newRow.parent_id.Should().Be(originalRowId);
        newRow.revoked_at.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRefresh_FamilyReplay_RevokesEntireFamily_And_Returns401()
    {
        // Arrange
        await EnsureSigningKeyAsync();
        const string password = "Acceptance!Replay99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-refresh-replay-{Guid.NewGuid():N}@example.com",
            password: password,
            mfaEnabled: false);

        using HttpClient client = CreateClient();
        (_, string originalRefreshValue) = await IssueTokensAsync(client, user.Email, password);

        // First refresh — rotates the family-root.
        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh"))
        {
            firstRequest.Headers.Add("Cookie", $"{CookieName}={originalRefreshValue}");
            using HttpResponseMessage firstResponse = await client.SendAsync(firstRequest);
            firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Act — present the ORIGINAL cookie a second time (replay).
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", $"{CookieName}={originalRefreshValue}");
        using HttpResponseMessage replayResponse = await client.SendAsync(replayRequest);

        // Assert — 401, family fully revoked, audit event written.
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var connection = OpenConnection();
        Guid familyId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT family_id FROM refresh_tokens WHERE user_id = @UserId LIMIT 1",
            new { UserId = user.Id });

        int unrevoked = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_at IS NULL",
            new { FamilyId = familyId });
        unrevoked.Should().Be(0, because: "family replay must sweep every row in the family");

        int familyReplayRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_reason = @Reason",
            new { FamilyId = familyId, Reason = RefreshTokenRevokedReason.FamilyReplay });
        familyReplayRows.Should().BeGreaterThan(0,
            because: "at least one row in the family must carry revoked_reason='family_replay'");

        int replayAudits = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
            new { EventType = AuditEventTypes.TokenRefreshReplayed, UserId = user.Id });
        replayAudits.Should().BeGreaterThan(0,
            because: "the replay sweeper must emit a token.refresh.replayed audit event");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRefresh_MissingCookie_Returns401()
    {
        await EnsureSigningKeyAsync();
        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/auth/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRefresh_UnknownHash_Returns401()
    {
        await EnsureSigningKeyAsync();
        using HttpClient client = CreateClient();

        // A 32-byte URL-safe Base64 string that the server never minted.
        const string unknownValue = "ZmFrZS1yZWZyZXNoLXRva2VuLW5vdC1pc3N1ZWQtYnktaGVpbWRhbGw";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"{CookieName}={unknownValue}");
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostRefresh_ExpiredRow_Returns401_WithoutFamilyRevoke()
    {
        // Arrange — seed a user and manually insert an already-expired refresh
        // row so we exercise the expiry branch in HandleRefreshAsync without
        // waiting 14 days.
        await EnsureSigningKeyAsync();
        HeimdallUser user = await SeedUserAsync(
            email: $"api-refresh-expired-{Guid.NewGuid():N}@example.com",
            password: "Acceptance!Expired99",
            mfaEnabled: false);

        Guid familyId = Guid.NewGuid();
        Guid rowId = familyId;
        const string plaintext = "expired-token-plaintext-value-9001";
        string hash = RefreshTokenHasher.ComputeHash(plaintext);
        DateTime now = DateTime.UtcNow;

        await using (var connection = OpenConnection())
        {
            await connection.ExecuteAsync(
                @"INSERT INTO refresh_tokens
                  (id, user_id, token_hash, family_id, parent_id, replaced_by,
                   issued_at, expires_at, revoked_at, revoked_reason)
                  VALUES
                  (@Id, @UserId, @Hash, @FamilyId, NULL, NULL,
                   @Issued, @Expires, NULL, NULL)",
                new
                {
                    Id = rowId,
                    UserId = user.Id,
                    Hash = hash,
                    FamilyId = familyId,
                    Issued = now.AddDays(-15),
                    Expires = now.AddMinutes(-5),
                });
        }

        using HttpClient client = CreateClient();

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"{CookieName}={plaintext}");
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 401, but the row is NOT revoked (expiry is not replay).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var verifyConnection = OpenConnection();
        var row = await verifyConnection.QuerySingleAsync<(DateTime? revoked_at, string? revoked_reason)>(
            "SELECT revoked_at, revoked_reason FROM refresh_tokens WHERE id = @Id",
            new { Id = rowId });
        row.revoked_at.Should().BeNull(because: "natural expiry must not revoke the row");
        row.revoked_reason.Should().BeNull();
    }

    // Skipped scenario — there is no protected /api/v1/* endpoint yet (Phase 5.6 step 14).
    // XFAIL: Phase 5.6 step 14
    // public async Task JwtBearer_DenylistFreeRequest_OnAuthenticatedProtectedEndpoint() { }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_factory.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Ensures the database has at least one active signing key — required by
    /// <see cref="JwtTokenIssuer"/>. Idempotent: subsequent calls see an existing
    /// current key and short-circuit.
    /// </summary>
    private async Task EnsureSigningKeyAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var signingKeys = scope.ServiceProvider.GetRequiredService<ISigningKeyService>();
        SigningKeyRecord? current = await signingKeys.GetCurrentSigningKeyAsync();
        if (current is not null)
        {
            return;
        }

        await signingKeys.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90));
    }

    private async Task<HeimdallUser> SeedUserAsync(string email, string password, bool mfaEnabled)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();

        var user = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = userManager.NormalizeEmail(email),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            TwoFactorEnabled = mfaEnabled,
        };

        IdentityResult result = await userManager.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(
            because: "test user seeding must succeed; errors: "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));

        return user;
    }

    private async Task<(string AccessToken, string RefreshCookieValue)> IssueTokensAsync(
        HttpClient client, string email, string password)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email = email, password = password });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"token issuance must succeed for {email}");

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string accessToken = body.RootElement.GetProperty("access_token").GetString()!;

        string setCookie = ExtractSetCookie(response, CookieName);
        string refreshValue = ExtractCookieValue(setCookie, CookieName);
        return (accessToken, refreshValue);
    }

    private async Task<Guid> GetSingleActiveRefreshTokenIdAsync(Guid userId)
    {
        await using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM refresh_tokens WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = userId });
    }

    /// <summary>
    /// Returns the full <c>Set-Cookie</c> header value emitted for the given
    /// cookie name. The header is returned verbatim — the refresh-token
    /// plaintext is URL-safe base64 and case-sensitive, so lower-casing it
    /// would silently corrupt the round-trip and the next <c>/refresh</c>
    /// would observe a different SHA-256 digest than the one persisted by
    /// the original <c>/token</c> call.
    /// </summary>
    private static string ExtractSetCookie(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            throw new InvalidOperationException("No Set-Cookie header on the response.");
        }

        foreach (string raw in values)
        {
            if (raw.StartsWith(cookieName + "=", StringComparison.Ordinal))
            {
                return raw;
            }
        }

        throw new InvalidOperationException(
            $"Set-Cookie header for '{cookieName}' not found. Present: "
                + string.Join("; ", values.Select(v => v.Split('=')[0])));
    }

    /// <summary>
    /// Extracts the value portion of a single <c>Set-Cookie</c> header for the
    /// named cookie — everything between <c>name=</c> and the first <c>;</c>.
    /// Preserves the original case of the value (required for the base64url
    /// refresh-token plaintext).
    /// </summary>
    private static string ExtractCookieValue(string setCookieHeader, string cookieName)
    {
        string prefix = cookieName + "=";
        int start = setCookieHeader.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Cookie '{cookieName}' not present in Set-Cookie header.");
        }

        start += prefix.Length;
        int end = setCookieHeader.IndexOf(';', start);
        return end < 0 ? setCookieHeader[start..] : setCookieHeader[start..end];
    }
}

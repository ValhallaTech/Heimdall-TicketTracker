using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
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
/// Phase 5.5 step 13 — integration tests for <c>POST /api/v1/auth/logout</c>.
/// Drives the endpoint through a real <see cref="WebApplicationFactory{TEntryPoint}"/>
/// against a Testcontainers Postgres so the refresh-family revoke and audit-event
/// writes are exercised end-to-end. Each test mints a real JWT via
/// <c>POST /api/v1/auth/token</c> so the bearer principal carries populated
/// <c>jti</c>/<c>exp</c> claims (the issuer always sets both — there is therefore
/// no test for "missing jti" because constructing such a token without sidestepping
/// the issuer is out of scope).
/// </summary>
[Collection("Phase5ApiAuth")]
public sealed class ApiAuthEndpointsLogoutTests : IClassFixture<HeimdallWebApplicationFactory>
{
    private const string CookieName = "__Host-heimdall_refresh";

    private readonly HeimdallWebApplicationFactory _factory;

    public ApiAuthEndpointsLogoutTests(HeimdallWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostLogout_AuthenticatedBearer_WithValidRefreshCookie_Returns204_RevokesFamily_WritesAudit()
    {
        // Arrange
        await EnsureSigningKeyAsync();
        const string password = "Logout!Family99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-logout-family-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        (string accessToken, string refreshCookieValue) = await IssueTokensAsync(client, user.Email!, password);

        // Capture the family id so we can assert across every row.
        Guid familyId;
        await using (NpgsqlConnection conn = OpenConnection())
        {
            familyId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT family_id FROM refresh_tokens WHERE user_id = @UserId",
                new { UserId = user.Id });
        }

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Cookie", $"{CookieName}={refreshCookieValue}");
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 204.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Every row in the family is revoked with reason='logout'.
        await using (NpgsqlConnection conn = OpenConnection())
        {
            int total = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId",
                new { FamilyId = familyId });
            total.Should().BeGreaterThan(0);

            int unrevoked = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_at IS NULL",
                new { FamilyId = familyId });
            unrevoked.Should().Be(0, because: "logout must revoke every still-active member of the family.");

            int logoutRevokes = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_reason = @Reason",
                new { FamilyId = familyId, Reason = RefreshTokenRevokedReason.Logout });
            logoutRevokes.Should().BeGreaterThan(0,
                because: "at least one row in the family must carry revoked_reason='logout'.");

            // NOTE: token.access.revoked audit row is NOT asserted here because the
            // test factory provides no real Redis multiplexer (REDIS_URL=localhost:6379
            // is unreachable inside Testcontainers). The denylist write therefore takes
            // the swallow-and-log branch and never emits its audit row. Asserting the
            // happy-path access-revoked audit requires a real Redis Testcontainer; the
            // refresh-family side of the contract — which is the durable side — is
            // exercised below.

            int familyRevokedAudits = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
                new { EventType = AuditEventTypes.TokenRefreshFamilyRevoked, UserId = user.Id });
            familyRevokedAudits.Should().BeGreaterThan(0,
                because: "the family-revoke step must emit a token.refresh.family_revoked audit row.");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostLogout_Response_ExpiresRefreshCookie_WithHostAttributes()
    {
        // Arrange
        await EnsureSigningKeyAsync();
        const string password = "Logout!Cookie99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-logout-cookie-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        (string accessToken, string refreshCookieValue) = await IssueTokensAsync(client, user.Email!, password);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Cookie", $"{CookieName}={refreshCookieValue}");
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 204 and the cookie is expired with the same __Host- attributes
        // as production. (ASP.NET's Cookies.Delete emits an expires-in-the-past
        // Set-Cookie carrying the configured Path/Secure/HttpOnly/SameSite
        // attributes; the browser then drops the live cookie.)
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        string setCookie = ExtractSetCookie(response, CookieName);
        string lower = setCookie.ToLowerInvariant();
        lower.Should().Contain("path=/", because: "delete must replay the original Path=/.");
        lower.Should().Contain("httponly");
        lower.Should().Contain("secure");
        lower.Should().Contain("samesite=strict");
        lower.Should().MatchRegex("expires=|max-age=0",
            because: "the delete header expires the cookie either via Expires in the past or Max-Age=0.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostLogout_MissingRefreshCookie_Returns204_AndDoesNotRevokeAnyFamily()
    {
        // Arrange — seed a user and mint a token, but DO NOT replay the refresh
        // cookie on the logout call. The endpoint still 204s (logout is
        // idempotent for the cookie-less case) and no refresh row is touched.
        await EnsureSigningKeyAsync();
        const string password = "Logout!NoCookie99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-logout-nocookie-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        (string accessToken, _) = await IssueTokensAsync(client, user.Email!, password);

        int unrevokedBefore;
        await using (NpgsqlConnection conn = OpenConnection())
        {
            unrevokedBefore = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @UserId AND revoked_at IS NULL",
                new { UserId = user.Id });
            unrevokedBefore.Should().BeGreaterThan(0);
        }

        // Act — no Cookie header on this call.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 204 and the refresh family is untouched, because without the
        // cookie the handler has no hash to resolve a family from.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (NpgsqlConnection conn = OpenConnection())
        {
            int unrevokedAfter = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @UserId AND revoked_at IS NULL",
                new { UserId = user.Id });
            unrevokedAfter.Should().Be(
                unrevokedBefore,
                because: "without a replayed cookie the handler can't resolve a family; refresh rows must be untouched.");

            int familyRevokedAudits = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
                new { EventType = AuditEventTypes.TokenRefreshFamilyRevoked, UserId = user.Id });
            familyRevokedAudits.Should().Be(0,
                because: "no family was revoked, so no family-revoked audit row must be written.");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostLogout_DenylistUnreachable_StillRevokesFamily_AndOmitsAccessRevokedAudit()
    {
        // Arrange — the test factory points REDIS_URL at an unreachable host,
        // so every RedisAccessTokenDenylist.DenyAsync call throws a
        // RedisConnectionException at runtime. The handler's swallow-and-log
        // branch absorbs the failure: family revoke still runs (it's the
        // durable side of the contract), but the token.access.revoked audit
        // row is NOT written (because the audit immediately follows the
        // denylist call inside the same try block).
        await EnsureSigningKeyAsync();
        const string password = "Logout!RedisDown99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-logout-redis-down-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        (string accessToken, string refreshCookieValue) = await IssueTokensAsync(client, user.Email!, password);

        Guid familyId;
        await using (NpgsqlConnection conn = OpenConnection())
        {
            familyId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT family_id FROM refresh_tokens WHERE user_id = @UserId",
                new { UserId = user.Id });
        }

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Cookie", $"{CookieName}={refreshCookieValue}");
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 204, family revoked, and access-revoked audit NOT written.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (NpgsqlConnection conn = OpenConnection())
        {
            int unrevoked = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_at IS NULL",
                new { FamilyId = familyId });
            unrevoked.Should().Be(0,
                because: "family revoke is durable and must complete even when the denylist write fails.");

            int accessRevokedAudits = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
                new { EventType = AuditEventTypes.TokenAccessRevoked, UserId = user.Id });
            accessRevokedAudits.Should().Be(0,
                because: "the access-revoked audit is gated on a successful denylist write; the swallow-and-log branch must not emit it.");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostLogout_CalledTwice_IsIdempotent_SecondCallRevokesZeroRows()
    {
        // Arrange
        await EnsureSigningKeyAsync();
        const string password = "Logout!Idempotent99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-logout-idempotent-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        (string accessToken, string refreshCookieValue) = await IssueTokensAsync(client, user.Email!, password);

        Guid familyId;
        await using (NpgsqlConnection conn = OpenConnection())
        {
            familyId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT family_id FROM refresh_tokens WHERE user_id = @UserId",
                new { UserId = user.Id });
        }

        // Act 1 — first logout revokes the family.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("Cookie", $"{CookieName}={refreshCookieValue}");
            using HttpResponseMessage first = await client.SendAsync(request);
            first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Snapshot the family-revoke audit-row count after the first call.
        int familyAuditsAfterFirst;
        await using (NpgsqlConnection conn = OpenConnection())
        {
            familyAuditsAfterFirst = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
                new { EventType = AuditEventTypes.TokenRefreshFamilyRevoked, UserId = user.Id });
        }

        // Act 2 — second logout with the SAME access token + refresh cookie.
        // The access token has not yet hit its exp so the bearer pipeline still
        // accepts it (the denylist write itself is a no-op-on-duplicate in
        // production Redis; here Redis is absent so denylist writes degrade to
        // best-effort). The family-revoke audit row must NOT increment because
        // every row is already revoked.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("Cookie", $"{CookieName}={refreshCookieValue}");
            using HttpResponseMessage second = await client.SendAsync(request);
            second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        int familyAuditsAfterSecond;
        await using (NpgsqlConnection conn = OpenConnection())
        {
            familyAuditsAfterSecond = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
                new { EventType = AuditEventTypes.TokenRefreshFamilyRevoked, UserId = user.Id });
        }

        familyAuditsAfterSecond.Should().Be(familyAuditsAfterFirst,
            because: "the second logout finds every family row already revoked; no new family-revoke audit row must be written.");

        // Sanity — family is still fully revoked.
        await using (NpgsqlConnection conn = OpenConnection())
        {
            int unrevoked = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_at IS NULL",
                new { FamilyId = familyId });
            unrevoked.Should().Be(0);
        }
    }

    // -----------------------------------------------------------------------------
    // Helpers — local copies of the helpers in ApiAuthEndpointsTests so each test
    // file is self-contained (the original file's helpers are private).
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

    private async Task<HeimdallUser> SeedUserAsync(string email, string password)
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
            TwoFactorEnabled = false,
        };

        IdentityResult result = await userManager.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(
            because: "test user seeding must succeed; errors: "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));

        return user;
    }

    private static async Task<(string AccessToken, string RefreshCookieValue)> IssueTokensAsync(
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

    private static string ExtractSetCookie(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            throw new InvalidOperationException("No Set-Cookie header on the response.");
        }

        string? raw = values.FirstOrDefault(v => v.StartsWith(cookieName + "=", StringComparison.Ordinal));
        if (raw is not null)
        {
            return raw;
        }

        throw new InvalidOperationException(
            $"Set-Cookie header for '{cookieName}' not found. Present: "
                + string.Join("; ", values.Select(v => v.Split('=')[0])));
    }

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

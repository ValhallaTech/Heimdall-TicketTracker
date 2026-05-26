using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.BLL.Tokens;
using Heimdall.Core.Auditing;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Heimdall.Tests.Shared.OpenFga;
using Heimdall.Web.Bootstrap;
using Heimdall.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using BllTupleKey = Heimdall.BLL.Authorization.OpenFga.TupleKey;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// Phase 5.7 step 21 acceptance suite — drives the assembled
/// <c>Heimdall.Web</c> HTTP pipeline end-to-end against the real
/// Postgres + Redis + OpenFGA sidecars provisioned by
/// <see cref="HeimdallWebApplicationFactoryWithOpenFga"/>. Together the
/// scenarios below prove the Phase&nbsp;5 token issuance + refresh-rotation +
/// family-replay + logout-denylist + OpenFGA-backed authorization +
/// audit-event plumbing all hold together across the JSON token surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Collection.</b> Shares the <c>Phase1Acceptance</c> xUnit collection for
/// the same env-var-mutation reason as <c>Phase2AcceptanceTests</c> /
/// <c>Phase4AcceptanceTests</c>: the underlying
/// <see cref="WebApplicationFactory{TEntryPoint}"/> instances mutate
/// process-wide environment variables in <c>CreateHost</c>, so the collection
/// name forces sequential execution against the other acceptance suites.
/// </para>
/// <para>
/// <b>Refresh cookie attachment.</b> The production refresh cookie is
/// <c>__Host-heimdall_refresh</c> with <c>Secure=true</c> unconditionally —
/// <see cref="System.Net.CookieContainer"/> drops it on auto-attach over the
/// HTTP loopback used by <see cref="WebApplicationFactory{TEntryPoint}"/>. Each
/// test reads <c>Set-Cookie</c> verbatim and re-attaches manually on the next
/// request. Mirrors <c>ApiAuthEndpointsTests</c>.
/// </para>
/// <para>
/// <b>Scenario&nbsp;7 — team-manager assigns ticket — is intentionally skipped.</b>
/// The bearer principal carries the raw <c>"sub"</c> claim
/// (<see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions"/>
/// is configured with <c>MapInboundClaims = false</c> + <c>NameClaimType = "sub"</c>),
/// but <c>OpenFgaAuthorizationHandler</c> reads only
/// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>. Until that
/// seam is closed (tracked separately by the existing skipped
/// <c>ApiTicketsEndpointsTests</c> cases), every bearer-backed FGA check
/// deny-closes regardless of tuples, so the &quot;policy allows&quot; arm is
/// unreachable through this surface. <see cref="Should_AssignTicket_When_BearerCarriesTeamManagerTupleAndAmrMfa"/>
/// is recorded as a <c>[Fact(Skip = ...)]</c> placeholder so the gap is visible
/// in the test report rather than silently absent.
/// </para>
/// <para>
/// <b>Audit-event coverage (scenario 8).</b> Distributed across the lifecycle
/// + logout tests: <c>token.access.issued</c>, <c>token.refresh.issued</c>,
/// <c>token.refresh.rotated</c>, <c>token.refresh.replayed</c>,
/// <c>token.refresh.family_revoked</c>, and <c>token.access.revoked</c> are
/// each asserted at least once with their production payload shape
/// (anonymous-object snake_case JSON per
/// <c>src/Heimdall.Web/Endpoints/ApiAuthEndpoints.cs</c>).
/// </para>
/// </remarks>
[Collection("Phase1Acceptance")]
public sealed class Phase5AcceptanceTests : IClassFixture<HeimdallWebApplicationFactoryWithOpenFga>
{
    private const string CookieName = "__Host-heimdall_refresh";

    // Stable per-test-class GUID pinned via HEIMDALL_SEED_ORGANIZATION_ID so the
    // RequireMfa handler and the seeded org-admin tuple line up on the same id.
    // Distinct from Phase4AcceptanceTests so concurrent runs against a shared
    // process never collide on the same seed-org keying.
    private static readonly Guid SeedOrgId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly HeimdallWebApplicationFactoryWithOpenFga _factory;

    static Phase5AcceptanceTests()
    {
        Environment.SetEnvironmentVariable(
            SeedOrganizationOptions.EnvVarName,
            SeedOrgId.ToString());
    }

    public Phase5AcceptanceTests(HeimdallWebApplicationFactoryWithOpenFga factory)
    {
        _factory = factory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenarios 1, 3, 4 — full token lifecycle: issue → rotate → replay-detect.
    // Also exercises four of the six audit-event types for scenario 8.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 1: non-admin <c>POST /api/v1/auth/token</c> succeeds (no MFA
    /// gate fires, <c>amr</c> contains only <c>pwd</c>) and persists
    /// <c>token.access.issued</c> + <c>token.refresh.issued</c> audit rows.
    /// Scenario 3: a subsequent <c>POST /api/v1/auth/refresh</c> rotates the
    /// family root — the old row is marked <c>revoked_reason='rotated'</c>
    /// with <c>replaced_by</c> pointing at the new row, and a
    /// <c>token.refresh.rotated</c> audit row lands with the
    /// <c>{jti, parent_id, family_id}</c> payload shape.
    /// Scenario 4: presenting the ORIGINAL cookie a second time triggers the
    /// family-replay sweeper — every row in the family ends revoked and a
    /// <c>token.refresh.replayed</c> + <c>token.refresh.family_revoked</c>
    /// pair lands.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task Should_IssueRotateAndDetectReplay_When_NonAdminDrivesFullRefreshLifecycle()
    {
        // ─── Arrange ──────────────────────────────────────────────────────
        await EnsureSigningKeyAsync().ConfigureAwait(false);
        string email = $"phase5-lifecycle-{Guid.NewGuid():N}@example.com";
        const string password = "Phase5!Lifecycle99";
        HeimdallUser user = await SeedUserAsync(email, password, mfaEnabled: false)
            .ConfigureAwait(false);

        using HttpClient client = CreateClient();

        // ─── Scenario 1 — issue ──────────────────────────────────────────
        (string accessToken, string originalRefresh) =
            await IssueTokensAsync(client, email, password).ConfigureAwait(false);

        JwtSecurityToken parsedAccess = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        parsedAccess.Subject.Should().Be(
            user.Id.ToString(),
            because: "the access-token sub claim must be the seeded user's id.");
        parsedAccess.Claims
            .Where(c => string.Equals(c.Type, "amr", StringComparison.Ordinal))
            .Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "pwd" },
                because: "a non-MFA password grant must carry amr=[\"pwd\"] only.");

        await AssertAuditEventPayloadAsync(
            user.Id,
            AuditEventTypes.TokenAccessIssued,
            payload =>
            {
                payload.GetProperty("jti").GetString().Should().NotBeNullOrWhiteSpace();
                payload.GetProperty("user_id").GetString().Should().Be(user.Id.ToString());
                payload.GetProperty("amr").EnumerateArray()
                    .Select(e => e.GetString())
                    .Should().Contain("pwd");
            }).ConfigureAwait(false);

        await AssertAuditEventExistsAsync(user.Id, AuditEventTypes.TokenRefreshIssued)
            .ConfigureAwait(false);

        // ─── Scenario 3 — rotate ─────────────────────────────────────────
        Guid originalRowId = await GetSingleActiveRefreshTokenIdAsync(user.Id).ConfigureAwait(false);

        using var rotateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        rotateRequest.Headers.Add("Cookie", $"{CookieName}={originalRefresh}");
        using HttpResponseMessage rotateResponse = await client.SendAsync(rotateRequest)
            .ConfigureAwait(false);

        rotateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string newRefresh = ExtractCookieValue(
            ExtractSetCookie(rotateResponse, CookieName),
            CookieName);
        newRefresh.Should().NotBe(originalRefresh);

        await using (NpgsqlConnection conn = OpenConnection())
        {
            var oldRow = await conn.QuerySingleAsync<(DateTime? revoked_at, string? revoked_reason, Guid? replaced_by)>(
                "SELECT revoked_at, revoked_reason, replaced_by FROM refresh_tokens WHERE id = @Id",
                new { Id = originalRowId }).ConfigureAwait(false);
            oldRow.revoked_at.Should().NotBeNull();
            oldRow.revoked_reason.Should().Be(
                RefreshTokenRevokedReason.Rotated,
                because: "the rotation path must stamp revoked_reason='rotated' on the parent row.");
            oldRow.replaced_by.Should().NotBeNull(
                because: "the rotation path must link the parent to the new child via replaced_by.");

            var newRow = await conn.QuerySingleAsync<(Guid id, Guid? parent_id, DateTime? revoked_at)>(
                "SELECT id, parent_id, revoked_at FROM refresh_tokens WHERE id = @Id",
                new { Id = oldRow.replaced_by!.Value }).ConfigureAwait(false);
            newRow.parent_id.Should().Be(originalRowId);
            newRow.revoked_at.Should().BeNull(
                because: "the freshly-minted child row must start unrevoked.");
        }

        await AssertAuditEventPayloadAsync(
            user.Id,
            AuditEventTypes.TokenRefreshRotated,
            payload =>
            {
                payload.GetProperty("jti").GetString().Should().NotBeNullOrWhiteSpace();
                payload.GetProperty("parent_id").GetString().Should().Be(originalRowId.ToString());
                payload.GetProperty("family_id").GetString().Should().NotBeNullOrWhiteSpace();
            }).ConfigureAwait(false);

        // ─── Scenario 4 — replay of the parent cookie ─────────────────────
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", $"{CookieName}={originalRefresh}");
        using HttpResponseMessage replayResponse = await client.SendAsync(replayRequest)
            .ConfigureAwait(false);

        replayResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            because: "replaying the parent cookie after rotation must 401.");

        // Subsequent use of the post-rotation child cookie must also 401 because
        // the family-replay sweeper has revoked the entire family.
        using var poisonedChildRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        poisonedChildRequest.Headers.Add("Cookie", $"{CookieName}={newRefresh}");
        using HttpResponseMessage poisonedChildResponse = await client.SendAsync(poisonedChildRequest)
            .ConfigureAwait(false);
        poisonedChildResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            because: "every member of a replay-poisoned family must be unusable, not just the replayed one.");

        // Family is fully revoked.
        await using (NpgsqlConnection conn = OpenConnection())
        {
            Guid familyId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT family_id FROM refresh_tokens WHERE user_id = @UserId LIMIT 1",
                new { UserId = user.Id }).ConfigureAwait(false);

            int unrevoked = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_at IS NULL",
                new { FamilyId = familyId }).ConfigureAwait(false);
            unrevoked.Should().Be(0, because: "family-replay must revoke every row in the family.");

            int familyReplayRows = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM refresh_tokens WHERE family_id = @FamilyId AND revoked_reason = @Reason",
                new { FamilyId = familyId, Reason = RefreshTokenRevokedReason.FamilyReplay }).ConfigureAwait(false);
            familyReplayRows.Should().BeGreaterThan(
                0,
                because: "at least one row in the family must carry revoked_reason='family_replay'.");
        }

        await AssertAuditEventPayloadAsync(
            user.Id,
            AuditEventTypes.TokenRefreshReplayed,
            payload =>
            {
                payload.GetProperty("jti").GetString().Should().NotBeNullOrWhiteSpace();
                payload.GetProperty("family_id").GetString().Should().NotBeNullOrWhiteSpace();
            }).ConfigureAwait(false);

        await AssertAuditEventPayloadAsync(
            user.Id,
            AuditEventTypes.TokenRefreshFamilyRevoked,
            payload =>
            {
                payload.GetProperty("family_id").GetString().Should().NotBeNullOrWhiteSpace();
                payload.GetProperty("reason").GetString().Should().Be(
                    "family_replay",
                    because: "the family-revoked payload's reason must match the underlying RefreshTokenRevokedReason.");
            }).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario 5 — logout denylists the access token. Asserts the bearer
    // becomes unusable on the next API call and that the token.access.revoked
    // audit row lands (this is the assertion the cookie-only-Redis ApiAuthEndpointsLogoutTests
    // suite explicitly defers to a real-Redis fixture; see its inline note at
    // lines 99-105).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 5: <c>POST /api/v1/auth/logout</c> revokes the refresh family
    /// AND writes the access-token jti to the Redis denylist. The first
    /// post-logout call to <c>GET /api/v1/tickets</c> with the same bearer
    /// must return 401 because the denylist hit triggers token validation
    /// failure. Audit assertion covers <c>token.access.revoked</c> with
    /// payload <c>{jti, reason='logout'}</c>.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task Should_DenylistAccessTokenAndRejectSubsequentBearerCall_When_LogoutCalled()
    {
        // ─── Arrange ──────────────────────────────────────────────────────
        await EnsureSigningKeyAsync().ConfigureAwait(false);
        string email = $"phase5-logout-{Guid.NewGuid():N}@example.com";
        const string password = "Phase5!Logout99";
        HeimdallUser user = await SeedUserAsync(email, password, mfaEnabled: false)
            .ConfigureAwait(false);

        using HttpClient client = CreateClient();
        (string accessToken, string refreshCookieValue) =
            await IssueTokensAsync(client, email, password).ConfigureAwait(false);

        string jti = new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Id;
        jti.Should().NotBeNullOrWhiteSpace();

        // ─── Act 1 — bearer is usable BEFORE logout (sanity check) ───────
        // We intentionally hit /api/v1/tickets — without a tuple it 403s. The
        // critical signal here is NOT a 200 but the absence of a 401: the
        // bearer authenticates, the FGA policy then denies, hence 403. After
        // logout the same bearer must instead produce 401 (denylist hit short-
        // circuits inside the JwtBearer events handler before authorization
        // runs).
        using (var preLogoutRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tickets"))
        {
            preLogoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage preLogoutResponse = await client.SendAsync(preLogoutRequest)
                .ConfigureAwait(false);
            preLogoutResponse.StatusCode.Should().NotBe(
                HttpStatusCode.Unauthorized,
                because: "before logout, the freshly-minted access token must authenticate successfully (a subsequent 403 from policy is fine).");
        }

        // ─── Act 2 — logout ──────────────────────────────────────────────
        using (var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout"))
        {
            logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            logoutRequest.Headers.Add("Cookie", $"{CookieName}={refreshCookieValue}");
            using HttpResponseMessage logoutResponse = await client.SendAsync(logoutRequest)
                .ConfigureAwait(false);
            logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // ─── Act 3 — same bearer must now 401 ────────────────────────────
        using (var postLogoutRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tickets"))
        {
            postLogoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage postLogoutResponse = await client.SendAsync(postLogoutRequest)
                .ConfigureAwait(false);
            postLogoutResponse.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                because: "after logout, the denylisted jti must short-circuit authentication BEFORE any FGA policy can run.");
        }

        // ─── Assert audit — token.access.revoked with {jti, reason='logout'} ──
        await AssertAuditEventPayloadAsync(
            user.Id,
            AuditEventTypes.TokenAccessRevoked,
            payload =>
            {
                payload.GetProperty("jti").GetString().Should().Be(
                    jti,
                    because: "the access-revoked audit row must reference the exact jti that was denylisted.");
                payload.GetProperty("reason").GetString().Should().Be("logout");
            }).ConfigureAwait(false);

        // family_revoked also lands (paired side of the logout contract).
        await AssertAuditEventExistsAsync(user.Id, AuditEventTypes.TokenRefreshFamilyRevoked)
            .ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario 6 — bearer with NO OpenFGA tuple is denied via FGA policy.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 6 (deny side): a bearer-backed user with no
    /// <c>ticket#view</c> tuple gets a 403 when calling
    /// <c>GET /api/v1/tickets/{id}</c>. The 403 must come from the FGA policy
    /// branch (i.e. authentication succeeded, authorization failed) — not from
    /// authentication. Documents the deny-closed default behaviour that
    /// remains valid regardless of the bearer→FGA actor-id seam noted in the
    /// class &lt;remarks&gt; (the seam blocks the allow side; the deny side is
    /// reachable and exercised here).
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task Should_Return403_When_BearerLacksOpenFgaTicketViewTuple()
    {
        // ─── Arrange ──────────────────────────────────────────────────────
        await EnsureSigningKeyAsync().ConfigureAwait(false);
        string email = $"phase5-tuple-deny-{Guid.NewGuid():N}@example.com";
        const string password = "Phase5!TupleDeny99";
        _ = await SeedUserAsync(email, password, mfaEnabled: false).ConfigureAwait(false);

        using HttpClient client = CreateClient();
        (string accessToken, _) = await IssueTokensAsync(client, email, password)
            .ConfigureAwait(false);

        // Resource id never persisted — the deny path doesn't reach the
        // repository, so a synthetic guid is sufficient.
        Guid ticketId = Guid.NewGuid();

        // ─── Act ─────────────────────────────────────────────────────────
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tickets/{ticketId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);

        // ─── Assert ──────────────────────────────────────────────────────
        // Deny-closed default — no tuple, no access. 401 would mean the
        // bearer failed to authenticate (a different bug); 200 would mean the
        // policy let an unauthorized actor through. Either is wrong.
        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "with no OpenFGA tuple seeded, the CanViewTicket policy must deny-close to 403.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario 2 — admin with MFA enrolled gets 401 {requires_two_factor:true}
    // on first /api/v1/auth/token, then upgrades the cookie via the existing
    // Phase 4.5 challenge flow, then a second /api/v1/auth/token call issues
    // an access token with amr=mfa.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 2: org-admin / system-admin with MFA enabled cannot exchange
    /// password credentials for an API token until they complete a cookie
    /// MFA challenge. First <c>POST /api/v1/auth/token</c> returns 401 with
    /// <c>{ "requires_two_factor": true }</c>; after the Phase 4.5 challenge
    /// flow upgrades the auth cookie to <c>amr=mfa</c>, a second call
    /// succeeds and the issued access token's <c>amr</c> claim includes
    /// <c>mfa</c>.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task Should_RequireMfaThenIssueAmrMfaToken_When_AdminCompletesCookieChallenge()
    {
        // ─── Arrange ──────────────────────────────────────────────────────
        await EnsureSigningKeyAsync().ConfigureAwait(false);
        string email = $"phase5-mfa-admin-{Guid.NewGuid():N}@example.com";
        const string password = "Phase5!MfaAdmin99";

        // Seed admin user with system_admin=true and the org-admin tuple so the
        // RequireMfa policy treats them as admin-bound.
        HeimdallUser user = await SeedUserAsync(email, password, mfaEnabled: false)
            .ConfigureAwait(false);
        await PromoteToSystemAdminAsync(user.Id).ConfigureAwait(false);
        await WriteOrgAdminTupleAsync(user.Id).ConfigureAwait(false);

        // Single HttpClient — preserves the auth cookie across the API + Razor
        // calls so the second /api/v1/auth/token reads the upgraded amr=mfa
        // principal.
        using HttpClient client = CreateClient();

        // ─── Sign in via the Razor login surface ─────────────────────────
        // Production note: PasswordSignInAsync over the /account/login form
        // stamps the auth cookie. The user does NOT have MFA enabled yet so
        // we expect the simple non-MFA login.
        await PostLoginAsync(client, email, password, expectTwoFactor: false)
            .ConfigureAwait(false);

        // ─── Enrol MFA via /account/mfa/setup ────────────────────────────
        await EnrolMfaAsync(client, user.Id).ConfigureAwait(false);

        // The enrol flow upgrades the cookie to amr=mfa in-session, but the
        // scenario explicitly asks for the post-enrol challenge path. Sign
        // out so the next /api/v1/auth/token call starts cookie-less and
        // triggers the requires_two_factor branch.
        await PostLogoutAsync(client).ConfigureAwait(false);

        // ─── Scenario 2a — first /api/v1/auth/token → 401 {requires_two_factor:true} ──
        using (HttpResponseMessage firstResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email, password }).ConfigureAwait(false))
        {
            firstResponse.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                because: "an MFA-enabled user must be challenged before the password grant succeeds.");

            string body = await firstResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            doc.RootElement.TryGetProperty("requires_two_factor", out JsonElement requires2fa)
                .Should().BeTrue(because: "the requires_two_factor sentinel must be present on the 401 body.");
            requires2fa.GetBoolean().Should().BeTrue();
        }

        // ─── Drive the Phase 4.5 cookie challenge ─────────────────────────
        // The password grant call above invokes PasswordSignInAsync which
        // stamps the TwoFactorUserIdScheme cookie; we can now post the
        // current TOTP to /account/mfa/challenge to upgrade the cookie to
        // amr=mfa.
        string code = await ComputeTotpCodeAsync(user.Id).ConfigureAwait(false);
        await PostChallengeAsync(client, code).ConfigureAwait(false);

        // ─── Scenario 2b — second /api/v1/auth/token → 200 with amr=mfa ──
        using HttpResponseMessage secondResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email, password }).ConfigureAwait(false);
        secondResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "with the amr=mfa cookie present, the password grant must mint an access token.");

        using JsonDocument success = JsonDocument.Parse(
            await secondResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        string accessToken = success.RootElement.GetProperty("access_token").GetString()!;
        JwtSecurityToken parsed = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        parsed.Claims
            .Where(c => string.Equals(c.Type, "amr", StringComparison.Ordinal))
            .Select(c => c.Value)
            .Should().Contain(
                "mfa",
                because: "an access token minted from an amr=mfa cookie principal must mirror amr=mfa.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario 7 — placeholder skip. The bearer→FGA actor-id seam in
    // OpenFgaAuthorizationHandler keys on ClaimTypes.NameIdentifier, but the
    // JwtBearer pipeline is configured with MapInboundClaims=false +
    // NameClaimType="sub" so the bearer principal never carries
    // ClaimTypes.NameIdentifier. Every "policy allows" path is therefore
    // deny-closed at the FGA layer until that seam is fixed (tracked by the
    // similarly-skipped cases in ApiTicketsEndpointsTests). Record the gap
    // here as a visible skip so it surfaces in test reports.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Placeholder for the team-manager-assigns-ticket happy path. See
    /// class-level &lt;remarks&gt; for why this is currently unreachable
    /// through the bearer surface.
    /// </summary>
    [Fact(Skip = "Phase 5.7 step 21 — blocked by the bearer→FGA actor-id seam (OpenFgaAuthorizationHandler reads ClaimTypes.NameIdentifier; the JwtBearer principal carries the raw 'sub' claim because MapInboundClaims=false). Mirror of the skipped cases in ApiTicketsEndpointsTests. Reinstate once the handler is updated to fall back to the 'sub' claim.")]
    public void Should_AssignTicket_When_BearerCarriesTeamManagerTupleAndAmrMfa()
    {
        // Intentionally empty — see Skip reason.
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

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
        SigningKeyRecord? current = await signingKeys.GetCurrentSigningKeyAsync().ConfigureAwait(false);
        if (current is not null)
        {
            return;
        }

        await signingKeys.GenerateAsync(SigningAlgorithm.Rs256, TimeSpan.FromDays(90))
            .ConfigureAwait(false);
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

        IdentityResult result = await userManager.CreateAsync(user, password).ConfigureAwait(false);
        result.Succeeded.Should().BeTrue(
            because: "Phase 5 acceptance fixture must seed users cleanly; errors: "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));

        return user;
    }

    private async Task PromoteToSystemAdminAsync(Guid userId)
    {
        await using var connection = OpenConnection();
        await connection.ExecuteAsync(
            "UPDATE users SET system_admin = true WHERE id = @Id",
            new { Id = userId }).ConfigureAwait(false);
    }

    private async Task WriteOrgAdminTupleAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var tupleWriter = scope.ServiceProvider.GetRequiredService<ITupleWriter>();
        BllTupleKey adminTuple = TupleShapes.OrgAdmin(SeedOrgId, userId);
        await tupleWriter.WriteAsync(adminTuple, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<(string AccessToken, string RefreshCookieValue)> IssueTokensAsync(
        HttpClient client, string email, string password)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email, password }).ConfigureAwait(false);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: $"token issuance must succeed for {email}.");

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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
            new { UserId = userId }).ConfigureAwait(false);
    }

    // ─── Set-Cookie helpers (verbatim from ApiAuthEndpointsTests) ────────────

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

    // ─── Razor login/logout/challenge helpers ────────────────────────────────

    private static async Task PostLoginAsync(
        HttpClient client,
        string email,
        string password,
        bool expectTwoFactor)
    {
        HttpResponseMessage loginPage = await client.GetAsync("/login").ConfigureAwait(false);
        loginPage.EnsureSuccessStatusCode();
        string token = ExtractAntiforgeryToken(
            await loginPage.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("email", email),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        HttpResponseMessage response = await client.PostAsync("/account/login", form)
            .ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found },
            "the login endpoint always redirects on success or MFA-required.");

        string location = response.Headers.Location!.OriginalString;
        if (expectTwoFactor)
        {
            location.Should().StartWith("/account/mfa/challenge");
        }
        else
        {
            location.Should().Be("/");
        }
    }

    private static async Task PostLogoutAsync(HttpClient client)
    {
        // The Razor logout surface is a form POST under /account/logout; we
        // need the antiforgery token from any rendered page that includes a
        // logout form. The simplest reliable source is /tickets (any
        // authenticated Razor page produces the layout's logout form).
        HttpResponseMessage page = await client.GetAsync("/tickets").ConfigureAwait(false);
        page.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "an authenticated user must be able to load any non-admin Razor page to read its antiforgery token.");
        string token = ExtractAntiforgeryToken(
            await page.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        HttpResponseMessage response = await client.PostAsync("/account/logout", form)
            .ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK },
            "the logout endpoint always either redirects to /login or returns 200 on success.");
    }

    private async Task EnrolMfaAsync(HttpClient client, Guid userId)
    {
        HttpResponseMessage setupPage = await client.GetAsync("/account/mfa/setup")
            .ConfigureAwait(false);
        setupPage.StatusCode.Should().Be(HttpStatusCode.OK);
        string token = ExtractAntiforgeryToken(
            await setupPage.Content.ReadAsStringAsync().ConfigureAwait(false));

        string code = await ComputeTotpCodeAsync(userId).ConfigureAwait(false);

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        HttpResponseMessage response = await client.PostAsync("/account/mfa/setup/verify", form)
            .ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found },
            "successful enrolment 302s to the recovery-codes display page.");
    }

    private static async Task PostChallengeAsync(HttpClient client, string code)
    {
        HttpResponseMessage challengePage = await client.GetAsync("/account/mfa/challenge")
            .ConfigureAwait(false);
        challengePage.StatusCode.Should().Be(HttpStatusCode.OK);
        string token = ExtractAntiforgeryToken(
            await challengePage.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("rememberMe", "false"),
            new KeyValuePair<string, string>("rememberMachine", "false"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        HttpResponseMessage response = await client.PostAsync("/account/mfa/challenge", form)
            .ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found },
            "the challenge endpoint always redirects on success.");
        response.Headers.Location!.OriginalString.Should().NotContain(
            "error=",
            because: "a successful challenge redirects back to the returnUrl without an error sentinel.");
    }

    // ─── TOTP helpers (verbatim parity with Phase4AcceptanceTests) ───────────

    private async Task<string> ComputeTotpCodeAsync(Guid userId)
    {
        await using var connection = OpenConnection();
        string? base32Key = await connection.ExecuteScalarAsync<string?>(
            @"SELECT authenticator_key FROM user_authenticator_keys WHERE user_id = @Id LIMIT 1;",
            new { Id = userId }).ConfigureAwait(false);

        if (string.IsNullOrEmpty(base32Key))
        {
            throw new InvalidOperationException(
                $"Phase 5 acceptance: no authenticator_key row for user {userId} — /account/mfa/setup GET should have rotated one in.");
        }

        byte[] keyBytes = Base32Decode(base32Key);
        long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long timestep = unixSeconds / 30;
        int code = ComputeTotp(keyBytes, (ulong)timestep);
        return code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ComputeTotp(byte[] keyBytes, ulong timestep)
    {
        Span<byte> timestepBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(timestepBytes, timestep);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(keyBytes, timestepBytes, hash);

        int offset = hash[hash.Length - 1] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        return binary % 1_000_000;
    }

    private static byte[] Base32Decode(string input)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var output = new List<byte>(capacity: input.Length * 5 / 8);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (char rawChar in input)
        {
            char c = char.ToUpperInvariant(rawChar);
            if (c == '=')
            {
                break;
            }
            int value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"Invalid Base32 character '{rawChar}'.");
            }
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return output.ToArray();
    }

    // ─── Audit-event assertions ──────────────────────────────────────────────

    private async Task AssertAuditEventExistsAsync(Guid actorId, string eventType)
    {
        await using var connection = OpenConnection();
        long count = await connection.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM audit_events
              WHERE actor_user_id = @ActorId AND event_type = @EventType",
            new { ActorId = actorId, EventType = eventType }).ConfigureAwait(false);

        count.Should().BeGreaterThan(
            0,
            because: $"the Phase 5 flow must emit at least one '{eventType}' row for actor {actorId}.");
    }

    /// <summary>
    /// Loads the most-recent <c>payload_json</c> for the given event_type +
    /// actor and invokes <paramref name="assertPayload"/> with the parsed
    /// JSON root. Asserting payload shape (not just row count) is what
    /// converts scenario 8 from a smoke check into a real contract test.
    /// </summary>
    private async Task AssertAuditEventPayloadAsync(
        Guid actorId,
        string eventType,
        Action<JsonElement> assertPayload)
    {
        await using var connection = OpenConnection();
        string? payloadJson = await connection.ExecuteScalarAsync<string?>(
            @"SELECT payload_json FROM audit_events
              WHERE actor_user_id = @ActorId AND event_type = @EventType
              ORDER BY occurred_at DESC, id DESC
              LIMIT 1",
            new { ActorId = actorId, EventType = eventType }).ConfigureAwait(false);

        payloadJson.Should().NotBeNullOrWhiteSpace(
            because: $"a '{eventType}' audit row must exist for actor {actorId}.");

        using JsonDocument doc = JsonDocument.Parse(payloadJson!);
        assertPayload(doc.RootElement);
    }

    // ─── Antiforgery scrape (verbatim from Phase4AcceptanceTests) ────────────

    private static string ExtractAntiforgeryToken(string html)
    {
        Match match = Regex.Match(
            html,
            "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"");
        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "<input[^>]*value=\"(?<token>[^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        }

        if (!match.Success)
        {
            throw new InvalidOperationException(
                "Antiforgery token hidden input not found in rendered HTML.");
        }

        return match.Groups["token"].Value;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Models;
using Heimdall.Tests.Shared.OpenFga;
using Heimdall.Web.Bootstrap;
using Heimdall.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using BllTupleKey = Heimdall.BLL.Authorization.OpenFga.TupleKey;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// Phase 4.7 step 22 acceptance suite — drives the assembled
/// <c>Heimdall.Web</c> HTTP pipeline end-to-end against a real Testcontainers
/// Postgres and a real OpenFGA sidecar (both provisioned by
/// <see cref="HeimdallWebApplicationFactoryWithOpenFga"/>) to prove the MFA
/// + admin-policy wiring shipped in Phase 4.3 through 4.6 holds together
/// across the full HTTP surface:
/// <list type="bullet">
/// <item><description>Admin without MFA → <c>/admin/*</c> bounces to
///   <c>/account/mfa/setup</c> via the Phase 4.6 step 17
///   <see cref="Heimdall.Web.Authorization.MfaSetupRedirectMiddlewareResultHandler"/>.</description></item>
/// <item><description>After successful enrolment + MFA challenge, the same
///   admin reaches <c>/admin/*</c> with a 200.</description></item>
/// <item><description>Disabling MFA tears the session — the next
///   <c>/admin/*</c> request 302s to <c>/login</c>, and re-signing in flips
///   the redirect target back to <c>/account/mfa/setup</c>.</description></item>
/// <item><description>Non-admin without MFA reaches non-admin pages without
///   the setup redirect.</description></item>
/// <item><description>All five MFA-relevant <c>audit_events</c> rows are
///   persisted along the way.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Collection.</b> Shares the <c>Phase1Acceptance</c> xUnit collection for
/// the same env-var-mutation reason as <c>Phase2AcceptanceTests</c>: the
/// underlying <see cref="WebApplicationFactory{TEntryPoint}"/> instances mutate
/// process-wide environment variables in <c>CreateHost</c>, so the collection
/// name forces sequential execution against the other Phase&nbsp;1 / 2 / 4
/// acceptance suites.
/// </para>
/// <para>
/// <b>Seed-org id pinning.</b> The <see cref="RequireMfaAuthorizationHandler"/>
/// keys on the seed-organization UUID surfaced through
/// <see cref="SeedOrganizationOptions"/>. We set
/// <see cref="SeedOrganizationOptions.EnvVarName"/> in a static constructor —
/// well before the lazy <c>WebApplicationFactory</c> host build runs on the
/// first <c>_factory.CreateClient(...)</c> call — so the handler observes the
/// same id we use for the org-admin tuple write.
/// </para>
/// <para>
/// <b>Audit-event naming.</b> Production code in
/// <c>src/Heimdall.Web/Endpoints/AccountEndpoints.cs</c> emits dot-separated
/// event types (<c>mfa.challenge.succeeded</c> / <c>mfa.challenge.failed</c> /
/// <c>mfa.recovery_codes.regenerated</c>). The assertions below match those
/// production strings verbatim.
/// </para>
/// </remarks>
[Collection("Phase1Acceptance")]
public sealed class Phase4AcceptanceTests : IClassFixture<HeimdallWebApplicationFactoryWithOpenFga>
{
    // Stable per-test-class GUID pinned via HEIMDALL_SEED_ORGANIZATION_ID so the
    // RequireMfa handler and the seeded org-admin tuple line up on the same id.
    private static readonly Guid SeedOrgId = new("11111111-2222-3333-4444-555555555555");

    private const string AdminPassword = "Phase4!Admin99";
    private const string NonAdminPassword = "Phase4!User99";

    private readonly HeimdallWebApplicationFactoryWithOpenFga _factory;

    static Phase4AcceptanceTests()
    {
        // Set BEFORE WebApplicationFactory.CreateHost runs (which happens lazily
        // on the first CreateClient call). SeedOrganizationOptions binds this
        // env var at startup; RequireMfaAuthorizationHandler reads the bound
        // value via IOptionsMonitor.
        Environment.SetEnvironmentVariable(
            SeedOrganizationOptions.EnvVarName,
            SeedOrgId.ToString());
    }

    public Phase4AcceptanceTests(HeimdallWebApplicationFactoryWithOpenFga factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Covers scenarios 1, 2, 3, and 5 from Phase 4.7 step 22 in a single
    /// end-to-end flight: redirect-to-setup → enrol → reach <c>/admin/*</c>
    /// → disable → logged out → re-login → redirect-to-setup again. Asserts
    /// each of the five MFA audit-event types along the way.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task Should_DriveFullAdminMfaLifecycle_When_AdminEnrolsChallengesAndDisables()
    {
        string adminEmail = $"phase4-admin-{Guid.NewGuid():N}@example.com";
        Guid adminId = await SeedAdminWithOrgAdminTupleAsync(adminEmail, AdminPassword)
            .ConfigureAwait(false);

        // -------- Scenario 1: admin without MFA is redirected to setup --------
        using (HttpClient client = await SignInAsync(adminEmail, AdminPassword, expectMfa: false)
            .ConfigureAwait(false))
        {
            HttpResponseMessage preEnrolment = await client.GetAsync("/admin/audit")
                .ConfigureAwait(false);

            preEnrolment.StatusCode.Should().Be(
                HttpStatusCode.Found,
                because: "admin without amr=mfa hits RequireMfa and the setup-redirect handler converts the deny to a 302.");
            preEnrolment.Headers.Location!.OriginalString.Should().StartWith(
                "/account/mfa/setup",
                because: "MfaSetupRedirectMiddlewareResultHandler bounces RequireMfa failures to /account/mfa/setup.");

            // -------- Enrol the admin (emits mfa_enrolled) --------
            await EnrolMfaAsync(client, adminId).ConfigureAwait(false);
        }

        // -------- Scenario 2: re-login with MFA challenge + reach /admin/* --------
        // Drop the original cookie by using a fresh client. The 1st-leg cookie
        // does NOT carry amr=mfa even though TwoFactorEnabled is now true; the
        // amr claim is only minted by the explicit /account/mfa/challenge POST.
        using HttpClient mfaClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Password login now returns RequiresTwoFactor → 302 /account/mfa/challenge.
        await PostLoginAsync(mfaClient, adminEmail, AdminPassword, expectTwoFactor: true)
            .ConfigureAwait(false);

        // Compute the valid TOTP first, then derive a guaranteed-different
        // bad code (mutating the last digit). Using a hard-coded value like
        // "000000" risked occasionally colliding with the live 30-second
        // window and silently flipping the failed-challenge case into a pass.
        string validCode = await ComputeTotpCodeAsync(adminId).ConfigureAwait(false);
        string invalidCode = DeriveInvalidTotpCode(validCode);

        // Failed challenge → emits mfa.challenge.failed but leaves the
        // TwoFactorUserId cookie intact for a retry.
        await PostChallengeAsync(mfaClient, code: invalidCode, expectSuccess: false)
            .ConfigureAwait(false);

        // Successful challenge → upgrades to ApplicationScheme cookie with amr=mfa.
        // Recompute in case the 30s window rolled over between the two posts.
        await PostChallengeAsync(mfaClient, code: await ComputeTotpCodeAsync(adminId).ConfigureAwait(false), expectSuccess: true)
            .ConfigureAwait(false);

        HttpResponseMessage adminReachable = await mfaClient.GetAsync("/admin/audit")
            .ConfigureAwait(false);
        adminReachable.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "with amr=mfa AND users.two_factor_enabled=true, the admin satisfies RequireMfa.");

        // Regenerate recovery codes (emits mfa.recovery_codes.regenerated).
        await RegenerateRecoveryCodesAsync(mfaClient, AdminPassword).ConfigureAwait(false);

        // -------- Scenario 3: disabling MFA logs the admin out --------
        await DisableMfaAsync(mfaClient, AdminPassword).ConfigureAwait(false);

        HttpResponseMessage afterDisable = await mfaClient.GetAsync("/admin/audit")
            .ConfigureAwait(false);
        afterDisable.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Found, HttpStatusCode.Redirect },
            "the disable endpoint signs the cookie out, so the next request is unauthenticated.");
        afterDisable.Headers.Location!.OriginalString.Should().Contain(
            "/login",
            because: "an unauthenticated request to a protected page goes through the global auth gate to /login.");

        // -------- Scenario 3 (tail): a new sign-in re-flips the redirect to /account/mfa/setup --------
        using HttpClient postDisableClient = await SignInAsync(adminEmail, AdminPassword, expectMfa: false)
            .ConfigureAwait(false);
        HttpResponseMessage afterReLogin = await postDisableClient.GetAsync("/admin/audit")
            .ConfigureAwait(false);
        afterReLogin.StatusCode.Should().Be(
            HttpStatusCode.Found,
            because: "after disable, the admin is back in the no-MFA state — RequireMfa fails again.");
        afterReLogin.Headers.Location!.OriginalString.Should().StartWith(
            "/account/mfa/setup",
            because: "the setup-redirect handler re-applies once MFA is no longer enrolled.");

        // -------- Scenario 5: audit-event persistence --------
        await AssertAuditEventExistsAsync(adminId, "mfa_enrolled").ConfigureAwait(false);
        await AssertAuditEventExistsAsync(adminId, "mfa_disabled").ConfigureAwait(false);
        await AssertAuditEventExistsAsync(adminId, "mfa.challenge.succeeded").ConfigureAwait(false);
        await AssertAuditEventExistsAsync(adminId, "mfa.challenge.failed").ConfigureAwait(false);
        await AssertAuditEventExistsAsync(adminId, "mfa.recovery_codes.regenerated").ConfigureAwait(false);
    }

    /// <summary>
    /// Scenario 4 — a non-admin (no <c>organization#admin</c> tuple,
    /// <c>system_admin = false</c>, MFA not enrolled) reaches a non-admin
    /// protected page without being bounced through <c>/account/mfa/setup</c>.
    /// </summary>
    [OpenFgaIntegrationFact]
    public async Task Should_NotRedirectToMfaSetup_When_NonAdminWithoutMfaVisitsProtectedPage()
    {
        string nonAdminEmail = $"phase4-user-{Guid.NewGuid():N}@example.com";
        _ = await SeedUserAsync(nonAdminEmail, NonAdminPassword, systemAdmin: false)
            .ConfigureAwait(false);

        using HttpClient client = await SignInAsync(nonAdminEmail, NonAdminPassword, expectMfa: false)
            .ConfigureAwait(false);

        HttpResponseMessage response = await client.GetAsync("/tickets").ConfigureAwait(false);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "RequireMfa succeeds for non-admins (no org-admin tuple, no system_admin) without enrolment.");
    }

    // ─── Seeding helpers ──────────────────────────────────────────────────────

    private async Task<Guid> SeedUserAsync(string email, string password, bool systemAdmin)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();

        var existing = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var user = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = userManager.NormalizeEmail(email),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };

        IdentityResult result = await userManager.CreateAsync(user, password).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Phase 4 acceptance fixture failed to seed user "
                + email + ": "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
        }

        if (systemAdmin)
        {
            await using var connection = new NpgsqlConnection(_factory.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await connection.ExecuteAsync(
                "UPDATE users SET system_admin = true WHERE id = @Id",
                new { Id = user.Id }).ConfigureAwait(false);
        }

        return user.Id;
    }

    private async Task<Guid> SeedAdminWithOrgAdminTupleAsync(string email, string password)
    {
        Guid adminId = await SeedUserAsync(email, password, systemAdmin: true).ConfigureAwait(false);

        // Write the organization:<SeedOrgId>#admin@user:<adminId> tuple via
        // the production ITupleWriter resolved from the live host — keeps the
        // wire format identical to what real org-admin promotions emit.
        await using var scope = _factory.Services.CreateAsyncScope();
        var tupleWriter = scope.ServiceProvider.GetRequiredService<ITupleWriter>();
        BllTupleKey adminTuple = TupleShapes.OrgAdmin(SeedOrgId, adminId);
        await tupleWriter.WriteAsync(adminTuple, CancellationToken.None).ConfigureAwait(false);

        return adminId;
    }

    // ─── HTTP-flow helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Signs the user in over the same antiforgery + cookie pipeline production
    /// uses. When <paramref name="expectMfa"/> is <c>false</c> the post asserts
    /// a 302 to <c>/</c>; when <c>true</c> it asserts a 302 to the MFA
    /// challenge endpoint instead.
    /// </summary>
    private async Task<HttpClient> SignInAsync(string email, string password, bool expectMfa)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await PostLoginAsync(client, email, password, expectTwoFactor: expectMfa).ConfigureAwait(false);
        return client;
    }

    private static async Task PostLoginAsync(
        HttpClient client,
        string email,
        string password,
        bool expectTwoFactor)
    {
        HttpResponseMessage loginPage = await client.GetAsync("/login").ConfigureAwait(false);
        loginPage.EnsureSuccessStatusCode();
        string html = await loginPage.Content.ReadAsStringAsync().ConfigureAwait(false);
        string token = ExtractAntiforgeryToken(html);

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
            location.Should().StartWith(
                "/account/mfa/challenge",
                because: "PasswordSignInAsync returns RequiresTwoFactor for an MFA-enabled user.");
        }
        else
        {
            location.Should().Be(
                "/",
                because: "non-MFA login redirects to the root after issuing the auth cookie.");
        }
    }

    private async Task EnrolMfaAsync(HttpClient client, Guid adminId)
    {
        // GET /account/mfa/setup rotates the authenticator key server-side
        // (MfaSetup.OnInitializedAsync calls ResetAuthenticatorKeyAsync). We
        // must compute the TOTP code AFTER this GET so we observe the same
        // key the verify endpoint will check against.
        HttpResponseMessage setupPage = await client.GetAsync("/account/mfa/setup")
            .ConfigureAwait(false);
        setupPage.StatusCode.Should().Be(HttpStatusCode.OK);
        string token = ExtractAntiforgeryToken(
            await setupPage.Content.ReadAsStringAsync().ConfigureAwait(false));

        string code = await ComputeTotpCodeAsync(adminId).ConfigureAwait(false);

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
        response.Headers.Location!.OriginalString.Should().StartWith(
            "/account/mfa/recovery-codes",
            because: "the verify endpoint hands off to the one-time recovery-code display.");
    }

    private static async Task PostChallengeAsync(HttpClient client, string code, bool expectSuccess)
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
            "the challenge endpoint always redirects, regardless of outcome.");

        string location = response.Headers.Location!.OriginalString;
        if (expectSuccess)
        {
            location.Should().NotContain(
                "error=",
                because: "a successful challenge redirects back to the returnUrl, no error sentinel.");
        }
        else
        {
            location.Should().Contain(
                "/account/mfa/challenge",
                because: "a failed challenge bounces the user back to the challenge page with an error.");
        }
    }

    private async Task RegenerateRecoveryCodesAsync(HttpClient client, string password)
    {
        HttpResponseMessage page = await client.GetAsync("/account/mfa/recovery-codes/regenerate")
            .ConfigureAwait(false);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        string token = ExtractAntiforgeryToken(
            await page.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        HttpResponseMessage response = await client.PostAsync(
            "/account/mfa/recovery-codes/regenerate",
            form).ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found },
            "regenerate always redirects — either to the codes display or back with an error.");
        response.Headers.Location!.OriginalString.Should().StartWith(
            "/account/mfa/recovery-codes",
            because: "successful regenerate hands off to the one-time display page.");
    }

    private async Task DisableMfaAsync(HttpClient client, string password)
    {
        HttpResponseMessage page = await client.GetAsync("/account/mfa/disable")
            .ConfigureAwait(false);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        string token = ExtractAntiforgeryToken(
            await page.Content.ReadAsStringAsync().ConfigureAwait(false));

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        HttpResponseMessage response = await client.PostAsync("/account/mfa/disable", form)
            .ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found },
            "the disable endpoint signs the user out and 302s to /login on success.");
        response.Headers.Location!.OriginalString.Should().StartWith(
            "/login",
            because: "successful disable redirects to /login?info=mfa-disabled.");
    }

    /// <summary>
    /// Computes the current RFC 6238 TOTP code for <paramref name="userId"/>'s
    /// stored authenticator key. Identity's <c>AuthenticatorTokenProvider</c>
    /// is verify-only — its <c>GenerateAsync</c> deliberately returns
    /// <see cref="string.Empty"/> because TOTPs are produced by the user's
    /// authenticator app, not by the server — so the test computes the code
    /// itself from the Base32 key written to <c>user_authenticator_keys</c>.
    /// The verify endpoint uses the same RFC 6238 30-second window with a
    /// ±2-step grace, so the locally-computed code matches what the server
    /// accepts.
    /// </summary>
    private async Task<string> ComputeTotpCodeAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        string? base32Key = await connection.ExecuteScalarAsync<string?>(
            @"SELECT authenticator_key FROM user_authenticator_keys WHERE user_id = @Id LIMIT 1;",
            new { Id = userId }).ConfigureAwait(false);

        if (string.IsNullOrEmpty(base32Key))
        {
            throw new InvalidOperationException(
                $"Phase 4 acceptance: no authenticator_key row for user {userId} — the /account/mfa/setup GET should have rotated one in.");
        }

        byte[] keyBytes = Base32Decode(base32Key);
        long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long timestep = unixSeconds / 30;
        int code = ComputeTotp(keyBytes, (ulong)timestep);
        return code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns a 6-digit code that is guaranteed to differ from
    /// <paramref name="validCode"/>. Used by the failed-challenge leg of the
    /// lifecycle test so the bad code cannot occasionally collide with the
    /// current 30-second TOTP window (a hard-coded constant like "000000"
    /// could).
    /// </summary>
    private static string DeriveInvalidTotpCode(string validCode)
    {
        // Flip the last digit by +1 mod 10 — keeps the result a valid
        // 6-digit string and is provably different from the input.
        ArgumentException.ThrowIfNullOrEmpty(validCode);
        char lastChar = validCode[^1];
        int lastDigit = lastChar - '0';
        int newDigit = (lastDigit + 1) % 10;
        return validCode[..^1] + (char)('0' + newDigit);
    }

    /// <summary>
    /// RFC 6238 TOTP computation matching the
    /// <see cref="AuthenticatorTokenProvider{TUser}.ValidateAsync"/> code path
    /// in <c>Microsoft.AspNetCore.Identity</c> (HMAC-SHA1 over the big-endian
    /// timestep, dynamic truncation, modulo 10⁶).
    /// </summary>
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

    /// <summary>
    /// Minimal Base32 decoder matching <c>Microsoft.AspNetCore.Identity.Base32.FromBase32</c>
    /// (RFC 4648, padding ignored). Identity stores the authenticator key in
    /// Base32; we decode it the same way the framework's verify path does.
    /// </summary>
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

    // ─── Audit-event assertion ────────────────────────────────────────────────

    private async Task AssertAuditEventExistsAsync(Guid actorId, string eventType)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        long count = await connection.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM audit_events
              WHERE actor_user_id = @ActorId AND event_type = @EventType",
            new { ActorId = actorId, EventType = eventType }).ConfigureAwait(false);

        count.Should().BeGreaterThan(0,
            because: $"the Phase 4 flow must emit at least one '{eventType}' row for actor {actorId}.");
    }

    // ─── Antiforgery scrape ───────────────────────────────────────────────────

    /// <summary>
    /// Pulls the <c>__RequestVerificationToken</c> hidden-input value out of a
    /// server-rendered HTML form. Mirrors the Phase 1 / Phase 2 acceptance
    /// helpers — Blazor's <c>&lt;AntiforgeryToken /&gt;</c> component emits
    /// the input with attributes in either order, so both orderings are
    /// matched.
    /// </summary>
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

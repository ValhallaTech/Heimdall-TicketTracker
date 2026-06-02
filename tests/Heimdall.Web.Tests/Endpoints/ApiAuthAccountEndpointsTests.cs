using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Core.Email;
using Heimdall.Core.Models;
using Heimdall.Web.Email;
using Heimdall.Web.Identity;
using Heimdall.Web.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// In-memory capturing <see cref="IEmailSender"/> test double for the Phase 6.3/6.4
/// JSON self-service auth suite. Records every <see cref="EmailMessage"/> it is asked
/// to deliver so tests can assert the matched-user reset path and the registration
/// confirmation path actually queue a message, while the anti-enumeration unknown-email
/// path queues nothing. Always succeeds — the production no-op fallback also succeeds.
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    /// <summary>Gets a snapshot of every message handed to <see cref="SendAsync"/>.</summary>
    public IReadOnlyCollection<EmailMessage> Messages => _messages.ToArray();

    /// <summary>Returns the messages addressed to the supplied recipient (case-insensitive).</summary>
    /// <param name="to">The recipient address to filter on.</param>
    public IReadOnlyList<EmailMessage> MessagesTo(string to) =>
        _messages.Where(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test-only <see cref="IStartupFilter"/> that stamps a unique submitted-email value into
/// <c>HttpContext.Items</c> at the very front of the pipeline (before
/// <c>UseRateLimiter</c>), so the <c>password-reset</c> policy — which partitions on
/// <c>(ip|submitted-email)</c> and only ever sees an empty email for JSON requests —
/// places every request in its own partition. Without this, the shared <c>ip|</c>
/// partition's 5-permits/10-minutes budget is exhausted within a single test class and
/// later forgot/reset requests are rejected with an empty-bodied <c>429</c>, which
/// <c>UseStatusCodePagesWithReExecute</c> then masks as a <c>400 text/html</c>. The
/// rate limiter itself is tested elsewhere; here it must not interfere with the handler
/// behavior under test.
/// </summary>
internal sealed class UniqueRateLimitPartitionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(static async (context, nextMiddleware) =>
            {
                context.Items[RateLimitFormEmailKeys.SubmittedEmailItemKey] =
                    Guid.NewGuid().ToString("N");
                await nextMiddleware().ConfigureAwait(false);
            });
            next(app);
        };
    }
}

/// <summary>
/// <see cref="IEmailSender"/> test double that always throws, used to drive the
/// "send failure must not roll back the account / must still return the generic success"
/// branches of the register and forgot-password handlers.
/// </summary>
public sealed class ThrowingEmailSender : IEmailSender
{
    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated SMTP transport failure.");
}

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> base for the Phase 6.3/6.4
/// JSON self-service auth suite. Reproduces just enough of the sealed
/// <see cref="Infrastructure.HeimdallWebApplicationFactory"/> wiring (env vars + a
/// dedicated Testcontainers Postgres + Development) and then overrides the three seams
/// the register / forgot-password / reset-password handlers branch on:
/// <list type="bullet">
/// <item><see cref="IEmailSender"/> → a shared <see cref="CapturingEmailSender"/>.</item>
/// <item><see cref="EmailFlowGate"/> → active/inactive per <see cref="GateActive"/>.</item>
/// <item><see cref="RegistrationOptions"/> → enabled/disabled per <see cref="RegistrationEnabled"/>.</item>
/// </list>
/// Each concrete factory owns its own Postgres container (distinct database name) so the
/// gate-active, registration-disabled, and gate-inactive permutations stay isolated; the
/// overrides are applied in <c>ConfigureServices</c> (last registration wins) so exactly
/// one host is built per factory.
/// </summary>
public abstract class ApiAuthAccountFactoryBase
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;

    /// <summary>Initializes the factory against a uniquely-named Postgres database.</summary>
    /// <param name="databaseName">The container database name (must be unique per factory).</param>
    protected ApiAuthAccountFactoryBase(string databaseName)
    {
        _postgres = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase(databaseName)
            .WithUsername("heimdall")
            .WithPassword("heimdall_test")
            .Build();
    }

    /// <summary>Gets the connection string for this factory's Postgres instance.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Gets the shared capturing email sender wired into the host by default.</summary>
    public CapturingEmailSender EmailSender { get; } = new();

    /// <summary>Gets a value indicating whether the email-flow gate reports active.</summary>
    protected abstract bool GateActive { get; }

    /// <summary>Gets a value indicating whether self-service registration is enabled.</summary>
    protected abstract bool RegistrationEnabled { get; }

    /// <summary>
    /// Resolves the <see cref="IEmailSender"/> to register. Defaults to the capturing
    /// sender; the send-failure factory overrides this to a throwing sender.
    /// </summary>
    protected virtual IEmailSender ResolveEmailSender() => EmailSender;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Mirror HeimdallWebApplicationFactory: export env vars BEFORE host creation
        // so Program.cs reads them at the same point production startup does.
        Environment.SetEnvironmentVariable("DATABASE_URL", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("REDIS_URL", "localhost:6379");
        Environment.SetEnvironmentVariable("SEED_DATABASE", "false");
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_EMAIL", string.Empty);
        Environment.SetEnvironmentVariable("HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD", string.Empty);
        return base.CreateHost(builder);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");

        // ConfigureServices runs after the app's own registrations, so these
        // last-registration-wins overrides decide resolution for the three seams
        // the JSON self-service handlers branch on. The default test host wires
        // NoOpEmailSender (gate inactive) + RegistrationOptions.Enabled=false, so
        // without these overrides every register/forgot/reset call would 404.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, UniqueRateLimitPartitionStartupFilter>();

            services.AddSingleton<IEmailSender>(ResolveEmailSender());

            services.AddSingleton(new EmailFlowGate(new EmailSenderRegistrationInfo
            {
                ChosenImplementation = GateActive ? "MailKitEmailSender" : "NoOpEmailSender",
            }));

            bool registrationEnabled = RegistrationEnabled;
            services.Configure<RegistrationOptions>(options => options.Enabled = registrationEnabled);
        });
    }
}

/// <summary>Gate active, registration enabled — the happy-path / validation factory.</summary>
public sealed class ApiAuthAccountActiveFactory : ApiAuthAccountFactoryBase
{
    public ApiAuthAccountActiveFactory()
        : base("heimdall_apiauth_account")
    {
    }

    /// <inheritdoc />
    protected override bool GateActive => true;

    /// <inheritdoc />
    protected override bool RegistrationEnabled => true;
}

/// <summary>Gate active, registration disabled — exercises the register "disabled → 404" branch.</summary>
public sealed class ApiAuthAccountRegistrationDisabledFactory : ApiAuthAccountFactoryBase
{
    public ApiAuthAccountRegistrationDisabledFactory()
        : base("heimdall_apiauth_regdisabled")
    {
    }

    /// <inheritdoc />
    protected override bool GateActive => true;

    /// <inheritdoc />
    protected override bool RegistrationEnabled => false;
}

/// <summary>Gate inactive — exercises the shared "email flow inactive → 404" branch on all three endpoints.</summary>
public sealed class ApiAuthAccountGateInactiveFactory : ApiAuthAccountFactoryBase
{
    public ApiAuthAccountGateInactiveFactory()
        : base("heimdall_apiauth_gateoff")
    {
    }

    /// <inheritdoc />
    protected override bool GateActive => false;

    /// <inheritdoc />
    protected override bool RegistrationEnabled => true;
}

/// <summary>Gate active, registration enabled, but the email sender always throws — exercises the
/// register / forgot-password "send failure must not roll back, still return generic success" branches.</summary>
public sealed class ApiAuthAccountEmailFailFactory : ApiAuthAccountFactoryBase
{
    public ApiAuthAccountEmailFailFactory()
        : base("heimdall_apiauth_emailfail")
    {
    }

    /// <inheritdoc />
    protected override bool GateActive => true;

    /// <inheritdoc />
    protected override bool RegistrationEnabled => true;

    /// <inheritdoc />
    protected override IEmailSender ResolveEmailSender() => new ThrowingEmailSender();
}

/// <summary>
/// Phase 6.3/6.4 — integration tests for the JSON self-service auth endpoints on
/// <see cref="Heimdall.Web.Endpoints.ApiAuthEndpoints"/>:
/// <c>POST /api/v1/auth/register</c>, <c>POST /api/v1/auth/forgot-password</c>, and
/// <c>POST /api/v1/auth/reset-password</c>. Each endpoint is driven through a real
/// <see cref="WebApplicationFactory{TEntryPoint}"/> against a Testcontainers Postgres so
/// Identity user creation, password-reset-token round-tripping, and the audit-event writes
/// are all exercised end-to-end. Each test seeds its own user (distinct email) so the
/// <c>(ip|email)</c> password-reset rate limiter never bleeds between cases.
/// </summary>
/// <remarks>
/// <para>
/// The suite joins the existing <c>"Phase5ApiAuth"</c> collection rather than declaring a
/// new one because, like every <c>HeimdallWebApplicationFactory</c>-shaped fixture, the
/// factories mutate the process-wide <c>DATABASE_URL</c> in <c>CreateHost</c>; that
/// collection is already <c>DisableParallelization = true</c>, so reusing it serializes
/// this suite against the other env-var-mutating collections and avoids the flaky
/// <c>23505 duplicate key … pg_type_typname_nsp_index</c> race.
/// </para>
/// <para>
/// Three factories are needed because <see cref="EmailFlowGate"/> and
/// <see cref="RegistrationOptions"/> are decided once at host build: a gate-active +
/// registration-enabled factory for the happy/validation paths, a gate-active +
/// registration-disabled factory for the register "disabled → 404" branch, and a
/// gate-inactive factory for the shared "email flow inactive → 404" branch.
/// </para>
/// </remarks>
[Collection("Phase5ApiAuth")]
public sealed class ApiAuthAccountEndpointsTests
    : IClassFixture<ApiAuthAccountActiveFactory>,
      IClassFixture<ApiAuthAccountRegistrationDisabledFactory>,
      IClassFixture<ApiAuthAccountGateInactiveFactory>,
      IClassFixture<ApiAuthAccountEmailFailFactory>
{
    private const string StrongPassword = "Acceptance!Account99";

    private readonly ApiAuthAccountActiveFactory _active;
    private readonly ApiAuthAccountRegistrationDisabledFactory _registrationDisabled;
    private readonly ApiAuthAccountGateInactiveFactory _gateInactive;
    private readonly ApiAuthAccountEmailFailFactory _emailFail;

    public ApiAuthAccountEndpointsTests(
        ApiAuthAccountActiveFactory active,
        ApiAuthAccountRegistrationDisabledFactory registrationDisabled,
        ApiAuthAccountGateInactiveFactory gateInactive,
        ApiAuthAccountEmailFailFactory emailFail)
    {
        _active = active;
        _registrationDisabled = registrationDisabled;
        _gateInactive = gateInactive;
        _emailFail = emailFail;
    }

    // -----------------------------------------------------------------------------
    // POST /api/v1/auth/register
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_ValidRequest_Returns200_ConfirmationPending_AndMailsConfirmation()
    {
        // Arrange
        string email = UniqueEmail("api-register-success");
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = StrongPassword, confirmPassword = StrongPassword });

        // Assert — response shape.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(response)).Should().Be("confirmation_pending");

        // The account is created unconfirmed.
        await using NpgsqlConnection connection = OpenConnection(_active);
        bool emailConfirmed = await connection.ExecuteScalarAsync<bool>(
            "SELECT email_confirmed FROM users WHERE normalized_email = @Email",
            new { Email = email.ToUpperInvariant() });
        emailConfirmed.Should().BeFalse(because: "registration creates the account with EmailConfirmed = false");

        // A confirmation email was queued to the new address.
        _active.EmailSender.MessagesTo(email).Should().ContainSingle()
            .Which.Subject.Should().Be("Confirm your Heimdall account");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_MalformedEmail_Returns400_InvalidEmail()
    {
        // Arrange
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email = "not-an-email", password = StrongPassword, confirmPassword = StrongPassword });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_email");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_PasswordMismatch_Returns400_PasswordMismatch()
    {
        // Arrange
        string email = UniqueEmail("api-register-mismatch");
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = StrongPassword, confirmPassword = StrongPassword + "X" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("password_mismatch");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_WeakPassword_Returns400_RegistrationFailed_WithCodes()
    {
        // Arrange
        string email = UniqueEmail("api-register-weak");
        const string weak = "weak";
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = weak, confirmPassword = weak });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("registration_failed");
        body.RootElement.GetProperty("codes").GetArrayLength()
            .Should().BeGreaterThan(0, because: "Identity surfaces the failing password-policy codes");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_DuplicateEmail_Returns400_RegistrationFailed()
    {
        // Arrange — seed an existing user, then re-register the same email.
        string email = UniqueEmail("api-register-duplicate");
        await SeedUserAsync(_active, email, StrongPassword);
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = StrongPassword, confirmPassword = StrongPassword });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("registration_failed");
        body.RootElement.GetProperty("codes").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_GateInactive_Returns404()
    {
        // Arrange
        string email = UniqueEmail("api-register-gateoff");
        using HttpClient client = CreateClient(_gateInactive);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = StrongPassword, confirmPassword = StrongPassword });

        // Assert — the handler returns 404 (see MaskedNotFound remarks for the host quirk).
        await AssertMaskedNotFoundAsync(response);

        // No account is created when the gate is inactive.
        await using NpgsqlConnection connection = OpenConnection(_gateInactive);
        int rows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE normalized_email = @Email",
            new { Email = email.ToUpperInvariant() });
        rows.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_RegistrationDisabled_Returns404()
    {
        // Arrange
        string email = UniqueEmail("api-register-disabled");
        using HttpClient client = CreateClient(_registrationDisabled);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = StrongPassword, confirmPassword = StrongPassword });

        // Assert — the handler returns 404 when RegistrationOptions.Enabled is false.
        await AssertMaskedNotFoundAsync(response);

        await using NpgsqlConnection connection = OpenConnection(_registrationDisabled);
        int rows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE normalized_email = @Email",
            new { Email = email.ToUpperInvariant() });
        rows.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_EmailSendFails_StillReturns200_AndKeepsAccount()
    {
        // Arrange — the host's email sender throws on send.
        string email = UniqueEmail("api-register-sendfail");
        using HttpClient client = CreateClient(_emailFail);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email, password = StrongPassword, confirmPassword = StrongPassword });

        // Assert — a confirmation-email failure must NOT roll back the account, and the
        // generic success response must be preserved so deliverability is not disclosed.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(response)).Should().Be("confirmation_pending");

        await using NpgsqlConnection connection = OpenConnection(_emailFail);
        int rows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE normalized_email = @Email",
            new { Email = email.ToUpperInvariant() });
        rows.Should().Be(1, because: "a send failure must not roll back the newly-created account");

        int auditRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType",
            new { EventType = "account.register.confirmation_email.failure" });
        auditRows.Should().BeGreaterThan(0);
    }

    // -----------------------------------------------------------------------------
    // POST /api/v1/auth/forgot-password
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgotPassword_GateInactive_Returns404()
    {
        // Arrange
        using HttpClient client = CreateClient(_gateInactive);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email = UniqueEmail("api-forgot-gateoff") });

        // Assert — the handler returns 404 when the email gate is inactive.
        await AssertMaskedNotFoundAsync(response);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgotPassword_KnownConfirmedUser_Returns200_MailsResetAndAudits()
    {
        // Arrange
        string email = UniqueEmail("api-forgot-known");
        HeimdallUser user = await SeedUserAsync(_active, email, StrongPassword);
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email });

        // Assert — generic OK, a reset mail queued, and the matched-user audit written.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(response)).Should().Be("ok");

        _active.EmailSender.MessagesTo(email).Should().ContainSingle()
            .Which.Subject.Should().Be("Reset your Heimdall password");

        await using NpgsqlConnection connection = OpenConnection(_active);
        int auditRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
            new { EventType = "password.reset.requested", UserId = user.Id });
        auditRows.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgotPassword_UnknownEmail_Returns200_AuditsUnknownAndSendsNoMail()
    {
        // Arrange — a never-seeded address with a unique domain so the audit query is exact.
        string domain = $"{Guid.NewGuid():N}.example.com";
        string email = $"api-forgot-unknown@{domain}";
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email });

        // Assert — identical generic OK, NO mail, and the unknown-email audit written.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(response)).Should().Be("ok");

        _active.EmailSender.MessagesTo(email).Should().BeEmpty(
            because: "an unknown email must not reveal existence by sending mail");

        await using NpgsqlConnection connection = OpenConnection(_active);
        int auditRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_events "
                + "WHERE event_type = @EventType AND payload->>'submitted_email_domain' = @Domain",
            new { EventType = "password.reset.requested.unknown_email", Domain = domain });
        auditRows.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgotPassword_KnownAndUnknownEmail_ProduceIdenticalResponse()
    {
        // Arrange — one real confirmed user and one never-seeded address.
        string knownEmail = UniqueEmail("api-forgot-equiv-known");
        await SeedUserAsync(_active, knownEmail, StrongPassword);
        string unknownEmail = UniqueEmail("api-forgot-equiv-unknown");
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage knownResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email = knownEmail });
        using HttpResponseMessage unknownResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email = unknownEmail });

        // Assert — anti-enumeration: status code AND body are indistinguishable.
        knownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string knownBody = await knownResponse.Content.ReadAsStringAsync();
        string unknownBody = await unknownResponse.Content.ReadAsStringAsync();
        unknownBody.Should().Be(knownBody);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgotPassword_KnownUser_EmailSendFails_StillReturns200_AndAuditsSendFailed()
    {
        // Arrange — a real confirmed user, but the host's email sender throws on send.
        string email = UniqueEmail("api-forgot-sendfail");
        HeimdallUser user = await SeedUserAsync(_emailFail, email, StrongPassword);
        using HttpClient client = CreateClient(_emailFail);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email });

        // Assert — the generic OK is preserved even when the reset mail cannot be sent,
        // and the send failure is audited as password.reset.send_failed.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(response)).Should().Be("ok");

        await using NpgsqlConnection connection = OpenConnection(_emailFail);
        int auditRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
            new { EventType = "password.reset.send_failed", UserId = user.Id });
        auditRows.Should().Be(1);
    }

    // -----------------------------------------------------------------------------
    // POST /api/v1/auth/reset-password
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_GateInactive_Returns404()
    {
        // Arrange
        using HttpClient client = CreateClient(_gateInactive);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                email = UniqueEmail("api-reset-gateoff"),
                token = "irrelevant",
                password = StrongPassword,
                confirmPassword = StrongPassword,
            });

        // Assert — the handler returns 404 when the email gate is inactive.
        await AssertMaskedNotFoundAsync(response);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_PasswordMismatch_Returns400_PasswordMismatch()
    {
        // Arrange
        string email = UniqueEmail("api-reset-mismatch");
        HeimdallUser user = await SeedUserAsync(_active, email, StrongPassword);
        string token = await GeneratePasswordResetTokenAsync(_active, user);
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                email,
                token,
                password = StrongPassword + "A",
                confirmPassword = StrongPassword + "B",
            });

        // Assert — mismatch short-circuits before any user lookup.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("password_mismatch");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_UnknownUser_Returns400_InvalidToken()
    {
        // Arrange — a never-seeded address.
        const string newPassword = "BrandNew!Reset123";
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                email = UniqueEmail("api-reset-unknown"),
                token = "any-token",
                password = newPassword,
                confirmPassword = newPassword,
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_token");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_BadToken_Returns400_InvalidToken()
    {
        // Arrange — a real user but a garbage token.
        string email = UniqueEmail("api-reset-badtoken");
        await SeedUserAsync(_active, email, StrongPassword);
        const string newPassword = "BrandNew!Reset123";
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                email,
                token = "this-is-not-a-valid-token",
                password = newPassword,
                confirmPassword = newPassword,
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_token");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_UnknownUserAndBadToken_ProduceIdenticalResponse()
    {
        // Arrange — unknown user vs. real user + bad token.
        const string newPassword = "BrandNew!Reset123";
        string knownEmail = UniqueEmail("api-reset-equiv-known");
        await SeedUserAsync(_active, knownEmail, StrongPassword);
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage unknownResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                email = UniqueEmail("api-reset-equiv-unknown"),
                token = "any-token",
                password = newPassword,
                confirmPassword = newPassword,
            });
        using HttpResponseMessage badTokenResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                email = knownEmail,
                token = "this-is-not-a-valid-token",
                password = newPassword,
                confirmPassword = newPassword,
            });

        // Assert — anti-enumeration: unknown-user and bad-token are indistinguishable.
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        badTokenResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string unknownBody = await unknownResponse.Content.ReadAsStringAsync();
        string badTokenBody = await badTokenResponse.Content.ReadAsStringAsync();
        badTokenBody.Should().Be(unknownBody);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_ValidToken_Returns200_AndChangesPassword()
    {
        // Arrange — seed a user and mint a real reset token via UserManager.
        string email = UniqueEmail("api-reset-success");
        HeimdallUser user = await SeedUserAsync(_active, email, StrongPassword);
        string token = await GeneratePasswordResetTokenAsync(_active, user);
        const string newPassword = "BrandNew!Reset123";
        using HttpClient client = CreateClient(_active);

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { email, token, password = newPassword, confirmPassword = newPassword });

        // Assert — generic OK, the success audit written, and the new password verifies.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(response)).Should().Be("ok");

        await using NpgsqlConnection connection = OpenConnection(_active);
        int auditRows = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType AND actor_user_id = @UserId",
            new { EventType = "password.reset.success", UserId = user.Id });
        auditRows.Should().Be(1);

        (await CheckPasswordAsync(_active, email, newPassword)).Should().BeTrue(
            because: "ResetPasswordAsync must persist the new password on success");
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Asserts a handler-emitted <c>404 NotFound</c> as observed at the HTTP boundary.
    /// The three JSON self-service handlers return <see cref="Microsoft.AspNetCore.Http.Results.NotFound()"/>
    /// (an empty body) when the email gate is inactive or registration is disabled.
    /// The host wires <c>UseStatusCodePagesWithReExecute("/not-found")</c>, which re-executes
    /// that empty-bodied response as the original JSON <c>POST</c> against the
    /// antiforgery-protected <c>/not-found</c> Razor page; antiforgery then rejects the
    /// non-form content type, masking the status as a <c>400 text/html</c>. This is the same
    /// documented pipeline quirk asserted in <c>ApiAuthzCheckEndpointTests</c>, so the
    /// observable contract is "a client error that is never a success", not a literal 404.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    private static async Task AssertMaskedNotFoundAsync(HttpResponseMessage response)
    {
        ((int)response.StatusCode).Should().BeInRange(
            400,
            499,
            because: "the handler returns 404; UseStatusCodePagesWithReExecute re-executes the empty-bodied "
                + "response against the antiforgery-protected /not-found page, surfacing a 4xx client error.");
        response.StatusCode.Should().NotBe(
            HttpStatusCode.OK,
            because: "a gated/disabled flow must never succeed.");

        // The response must NOT be a handler success/validation envelope — i.e. it never
        // carries a status/error JSON body, confirming the request was rejected before the
        // handler's body-producing branches.
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"status\"");
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static NpgsqlConnection OpenConnection(ApiAuthAccountFactoryBase factory)
    {
        var connection = new NpgsqlConnection(factory.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task<HeimdallUser> SeedUserAsync(
        ApiAuthAccountFactoryBase factory, string email, string password)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();

        var user = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = userManager.NormalizeEmail(email),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };

        IdentityResult result = await userManager.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(
            because: "test user seeding must succeed; errors: "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));

        return user;
    }

    private static async Task<string> GeneratePasswordResetTokenAsync(
        ApiAuthAccountFactoryBase factory, HeimdallUser user)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();
        HeimdallUser loaded = (await userManager.FindByIdAsync(user.Id.ToString()))!;
        return await userManager.GeneratePasswordResetTokenAsync(loaded);
    }

    private static async Task<bool> CheckPasswordAsync(
        ApiAuthAccountFactoryBase factory, string email, string password)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();
        HeimdallUser? user = await userManager.FindByEmailAsync(email);
        return user is not null && await userManager.CheckPasswordAsync(user, password);
    }

    private static async Task<string?> ReadStatusAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("status").GetString();
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString();
    }
}

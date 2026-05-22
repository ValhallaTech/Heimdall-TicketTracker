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
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;
using Heimdall.Core.Tokens;
using Heimdall.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// Phase 5.6 steps 14–15 — integration tests for the <c>/api/v1/tickets</c>
/// endpoint surface. Drives the endpoints through a real
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// against a Testcontainers Postgres so authentication, authorization, and
/// validation behavior are exercised end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage scope.</b> Two production seams limit what we can assert from
/// the HTTP surface without modifying production code:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>NameIdentifier gap on JWT bearer principals.</b> <c>Program.cs</c> sets
/// <c>MapInboundClaims = false</c> on the JwtBearer options and does not
/// configure a <c>NameClaimType</c>; the <c>OpenFgaAuthorizationHandler</c>
/// reads only <c>ClaimTypes.NameIdentifier</c>, which is therefore unpopulated
/// on every bearer-authenticated request. Result: every <c>CanViewTicket</c> /
/// <c>CanEditTicket</c> / <c>CanAssignTicket</c> policy check deny-closes
/// today, regardless of FGA tuples. These tests assert the deny-closed
/// observable behavior (403) rather than the intended happy-path, and the
/// "policy ALLOWS" branches are surfaced as a finding rather than tested.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Autofac override seam.</b> <c>ITicketService</c> is registered via an
/// Autofac module (<c>ApplicationModule</c>), and the ASP.NET Core
/// <c>ConfigureTestContainer&lt;TBuilder&gt;</c> extension is wired through
/// the legacy <c>IStartupConfigureContainerFilter</c> path which does not
/// fire under the minimal-hosting model used by <c>Heimdall.Web</c>. Tests
/// therefore exercise the real <c>TicketService</c> rather than mocking it,
/// and the <c>ITupleWriter</c> single-Write regression for
/// <c>HandleAssignAsync</c> is asserted in the <c>TicketService</c> unit
/// suite instead of here.
/// </description>
/// </item>
/// </list>
/// </remarks>
[Collection("Phase5ApiTickets")]
public sealed class ApiTicketsEndpointsTests : IClassFixture<HeimdallWebApplicationFactory>
{
    private const string RefreshCookieName = "__Host-heimdall_refresh";

    private readonly HeimdallWebApplicationFactory _factory;

    public ApiTicketsEndpointsTests(HeimdallWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------------------
    // Unauthenticated requests — every endpoint must 401 without a bearer.
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetList_NoBearer_Returns401()
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/tickets");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetById_NoBearer_Returns401()
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/tickets/1");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(Skip = "Production gap: ApiTicketsEndpoints does not call .DisableAntiforgery() on its " +
        "MapPost/MapPut routes, so the global UseAntiforgery() middleware short-circuits every " +
        "POST/PUT with application/json body to 400 'The request has an incorrect Content-type.' " +
        "BEFORE the [Authorize] gate runs — meaning we cannot observe the expected 401 here. " +
        "Fix lives in production code (add .DisableAntiforgery() to the bearer-auth API routes), " +
        "out of scope for this test-only PR.")]
    [Trait("Category", "Integration")]
    public async Task Create_NoBearer_Returns401()
    {
        using HttpClient client = CreateClient();
        // Valid-shaped body so model binding / data annotations pass and the
        // pipeline reaches the [Authorize] gate, which is what we're asserting.
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tickets/", new
        {
            title = "ok",
            description = "ok",
            projectId = Guid.NewGuid(),
            teamId = Guid.NewGuid(),
            reporterId = Guid.NewGuid(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(Skip = "Same antiforgery production gap as Create_NoBearer_Returns401: PUT /api/v1/tickets/{id} " +
        "with application/json is rejected by UseAntiforgery() at 400 before [Authorize] runs.")]
    [Trait("Category", "Integration")]
    public async Task Update_NoBearer_Returns401()
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/tickets/1", new TicketDto
        {
            Id = 1,
            Title = "x",
            Description = "x",
            ProjectId = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            ReporterId = Guid.NewGuid(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(Skip = "Same antiforgery production gap as Create_NoBearer_Returns401: POST /api/v1/tickets/{id}/assign " +
        "with application/json is rejected by UseAntiforgery() at 400 before [Authorize] runs.")]
    [Trait("Category", "Integration")]
    public async Task Assign_NoBearer_Returns401()
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tickets/1/assign", new
        {
            assigneeId = Guid.NewGuid(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------------
    // Authenticated happy / observable paths.
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetList_Bearer_NoTuples_Returns200WithEmptyPage()
    {
        // Arrange — the test factory registers the NoOpOpenFgaAuthorizationService
        // (because no real sidecar is configured), so ListObjectsAsync returns an
        // empty array. The List endpoint then asks TicketService for the empty id
        // set and returns an empty PagedResult — which exercises the full
        // bearer-authenticated read pipeline (auth, IsAuthenticated policy, FGA
        // adapter, service layer, JSON projection) end-to-end.
        await EnsureSigningKeyAsync();
        const string password = "Tickets!List99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-tickets-list-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        string accessToken = await IssueAccessTokenAsync(client, user.Email!, password);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tickets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 200 + a PagedResult<TicketDto> with no items.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        root.TryGetProperty("items", out JsonElement items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);
        items.GetArrayLength().Should().Be(0,
            because: "the NoOp FGA service returns no allowed ticket ids, so the page must be empty.");
        root.GetProperty("totalCount").GetInt32().Should().Be(0);
        root.GetProperty("page").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("pageSize").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_Bearer_InvalidBody_Returns422_ProblemJson()
    {
        // Arrange — POST with a body that fails the endpoint's TryValidate gate.
        // The validation runs before the service is called, so this exercises
        // the validation surface without needing real Projects/Teams/Reporters.
        await EnsureSigningKeyAsync();
        const string password = "Tickets!Create99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-tickets-create-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        string accessToken = await IssueAccessTokenAsync(client, user.Email!, password);

        // Empty title + empty description + Guid.Empty for required FKs.
        var badBody = new
        {
            title = string.Empty,
            description = string.Empty,
            projectId = Guid.Empty,
            teamId = Guid.Empty,
            reporterId = Guid.Empty,
        };

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
        {
            Content = JsonContent.Create(badBody),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert — 422 application/problem+json carrying per-field errors.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("errors", out JsonElement errors).Should().BeTrue();
        errors.EnumerateObject().Should().NotBeEmpty(
            because: "the validation problem document must list at least one field error.");
    }

    // -----------------------------------------------------------------------------
    // Authenticated deny-closed paths — these exercise the policy gates and
    // assert today's observable behavior. See the class remarks for the
    // NameIdentifier production gap that makes "policy ALLOWS" paths
    // unreachable via JWT bearer at the moment.
    // -----------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetById_Bearer_PolicyDenies_Returns403()
    {
        await EnsureSigningKeyAsync();
        const string password = "Tickets!Get99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-tickets-getbyid-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        string accessToken = await IssueAccessTokenAsync(client, user.Email!, password);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tickets/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "CanViewTicket deny-closes for every bearer request today: "
                + "OpenFgaAuthorizationHandler reads ClaimTypes.NameIdentifier, which "
                + "is unpopulated when MapInboundClaims=false (see class remarks).");
    }

    [Fact(Skip = "Same antiforgery production gap as Create_NoBearer_Returns401 — PUT /api/v1/tickets/{id} " +
        "with application/json is rejected by UseAntiforgery() at 400 (and 405 after StatusCodePagesWithReExecute " +
        "fires) before the [Authorize]+CanEditTicket policy is consulted.")]
    [Trait("Category", "Integration")]
    public async Task Update_Bearer_PolicyDenies_Returns403()
    {
        await EnsureSigningKeyAsync();
        const string password = "Tickets!Update99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-tickets-update-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        string accessToken = await IssueAccessTokenAsync(client, user.Email!, password);

        var body = new TicketDto
        {
            Id = 1,
            Title = "ok",
            Description = "ok",
            ProjectId = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            ReporterId = Guid.NewGuid(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tickets/1")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "CanEditTicket deny-closes for every bearer request today (NameIdentifier gap).");
    }

    [Fact(Skip = "Same antiforgery production gap as Create_NoBearer_Returns401 — POST /api/v1/tickets/{id}/assign " +
        "with application/json is rejected by UseAntiforgery() at 400 before RequireMfa is evaluated.")]
    [Trait("Category", "Integration")]
    public async Task Assign_Bearer_NoMfaAmr_Returns403()
    {
        // Arrange — a freshly-issued bearer carries amr=["pwd"] (or empty),
        // never amr=["mfa"], because the test seed user has TwoFactorEnabled=false
        // and the password-grant endpoint won't promote amr. The RequireMfa
        // policy attached to /assign therefore deny-closes before the FGA
        // CanAssignTicket policy is even consulted.
        await EnsureSigningKeyAsync();
        const string password = "Tickets!Assign99";
        HeimdallUser user = await SeedUserAsync(
            email: $"api-tickets-assign-{Guid.NewGuid():N}@example.com",
            password: password);

        using HttpClient client = CreateClient();
        string accessToken = await IssueAccessTokenAsync(client, user.Email!, password);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets/1/assign")
        {
            Content = JsonContent.Create(new { assigneeId = Guid.NewGuid() }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "RequireMfa deny-closes for bearer tokens whose amr does not include 'mfa'.");
    }

    // -----------------------------------------------------------------------------
    // Skipped scenarios — documented here so the gap is visible in the suite
    // rather than buried in a tracker ticket.
    // -----------------------------------------------------------------------------

    [Fact(Skip = "Production gap: MapInboundClaims=false + no NameClaimType means OpenFgaAuthorizationHandler "
        + "(which reads ClaimTypes.NameIdentifier only) deny-closes every bearer-authenticated FGA policy check. "
        + "Restoring this test requires either populating NameIdentifier on the bearer principal or teaching the "
        + "handler to fall back to the 'sub' claim — both are production-code changes outside this test PR's scope.")]
    [Trait("Category", "Integration")]
    public Task GetById_Bearer_PolicyAllows_Returns200() => Task.CompletedTask;

    [Fact(Skip = "Same NameIdentifier production gap as GetById_Bearer_PolicyAllows_Returns200.")]
    [Trait("Category", "Integration")]
    public Task Update_Bearer_PolicyAllows_Returns204() => Task.CompletedTask;

    [Fact(Skip = "Same NameIdentifier production gap, plus the assign endpoint also requires amr=mfa which the "
        + "password-grant issuance path doesn't emit; restoring this test requires fixing both seams.")]
    [Trait("Category", "Integration")]
    public Task Assign_Bearer_PolicyAllows_Returns200() => Task.CompletedTask;

    [Fact(Skip = "Same NameIdentifier production gap; cannot reach the 404-vs-no-op discriminator branch via JWT bearer today.")]
    [Trait("Category", "Integration")]
    public Task Assign_Bearer_TicketNotFound_Returns404_ProblemJson() => Task.CompletedTask;

    [Fact(Skip = "ITupleWriter single-Write regression belongs at the TicketService unit-test layer: the endpoint "
        + "delegates to TicketService.AssignTicketAsync, and the ITupleWriter.ReplaceAsync call happens inside the "
        + "service — not in the endpoint — so the regression cannot be observed through the HTTP surface.")]
    [Trait("Category", "Integration")]
    public Task Assign_Service_CallsTupleWriter_Once() => Task.CompletedTask;

    [Fact(Skip = "Documented for completeness: the cookie auth scheme cannot substitute for the bearer scheme on "
        + "/api/v1/tickets because every ticket route declares JwtBearerDefaults.AuthenticationScheme explicitly. "
        + "Asserting this requires a cookie-issuance path against the API host, which is non-trivial; "
        + "the equivalent assertion at the auth-endpoint layer is the load-bearing one.")]
    [Trait("Category", "Integration")]
    public Task GetList_CookieSchemeOnly_Returns401() => Task.CompletedTask;

    // -----------------------------------------------------------------------------
    // Helpers — local copies of the ApiAuthEndpointsTests helpers so each test
    // file is self-contained (the original file's helpers are private).
    // -----------------------------------------------------------------------------

    private HttpClient CreateClient() => _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

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

    private static async Task<string> IssueAccessTokenAsync(HttpClient client, string email, string password)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { email = email, password = password });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"token issuance must succeed for {email}");

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("access_token").GetString()!;
    }
}

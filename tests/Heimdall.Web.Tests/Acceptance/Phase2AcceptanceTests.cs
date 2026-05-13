using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// Phase 2.10 step 29 — end-to-end acceptance test for the route → assign →
/// audit-feed loop introduced in Phase 2.7 / 2.8. Boots the real
/// <c>Heimdall.Web</c> host via <see cref="HeimdallWebApplicationFactory"/>
/// against a Testcontainers Postgres, signs in as the bootstrap system admin,
/// seeds an <c>organization → team → project → ticket</c> graph plus three
/// non-admin team members at distinct roles, and exercises the routing and
/// assignment surfaces of <see cref="ITicketService"/> end-to-end. The
/// claim-ticket surface is covered separately by the §5 policy matrix in
/// <see cref="Heimdall.DAL.Tests.Acceptance.PolicyMatrixIntegrationTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Audit feed verification queries <c>audit_events</c> directly via Dapper
/// rather than scraping <c>/admin/audit</c>'s rendered HTML — that page is
/// rendered with <c>@rendermode InteractiveServer</c>, so the audit rows are
/// fetched in <c>OnInitializedAsync</c> after the initial GET response is
/// streamed. Asserting on the database is more deterministic and dovetails
/// with the existing pattern in
/// <see cref="Phase1AcceptanceTests"/> /
/// <see cref="Heimdall.DAL.Tests.Acceptance.PolicyMatrixIntegrationTests"/>.
/// The page itself is exercised separately by a GET request that asserts the
/// admin gate accepts the seeded system-admin cookie.
/// </para>
/// <para>
/// Org / team / project / membership rows are seeded directly through the
/// registered repositories resolved from <see cref="WebApplicationFactory{TEntryPoint}.Services"/>
/// — the admin write surfaces from PR #32 are Blazor InteractiveServer pages
/// without a REST shape, so the orchestrator brief explicitly permits the
/// repository shortcut for end-to-end value. Exercising the admin pages
/// themselves is a separate UI concern owned by the bUnit suite under
/// <c>tests/Heimdall.Web.Tests/Components/</c>.
/// </para>
/// </remarks>
// The acceptance suites share a Testcontainers Postgres + a global env-var
// dance (DATABASE_URL etc. set in HeimdallWebApplicationFactory.CreateHost) so
// Phase 1 and Phase 2 must NOT run in parallel — running them in separate
// xUnit collections triggers env-var races that surface as migration /
// connection-refused failures. Sharing "Phase1Acceptance" forces sequential
// execution. (The orchestrator brief asked for a new collection name; that
// turned out to be incompatible with the WAF's process-wide env-var seam.)
[Collection("Phase1Acceptance")]
public sealed class Phase2AcceptanceTests : IClassFixture<HeimdallWebApplicationFactory>
{
    private const string AdminEmail = "phase2-admin@example.com";
    private const string AdminPassword = "Phase2!Admin99";

    private readonly HeimdallWebApplicationFactory _factory;

    public Phase2AcceptanceTests(HeimdallWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Should_RouteAndAssignTicket_When_BootstrapAdminDrivesPhase2Surface()
    {
        // -------------------- Arrange: identities --------------------
        var adminId = await SeedSystemAdminAsync(_factory.Services, AdminEmail, AdminPassword);

        var managerId = await SeedUserAsync(_factory.Services, "phase2-manager@example.com", "Phase2!Manager99");
        var memberId = await SeedUserAsync(_factory.Services, "phase2-member@example.com", "Phase2!Member99");
        var viewerId = await SeedUserAsync(_factory.Services, "phase2-viewer@example.com", "Phase2!Viewer99");

        // -------------------- Arrange: hierarchy --------------------
        // Repositories rather than admin pages: the admin write surfaces from
        // PR #32 are Blazor server-rendered pages, not REST endpoints, so this
        // test takes the shortcut the orchestrator brief explicitly permits.
        await using var scope = _factory.Services.CreateAsyncScope();
        var orgRepo = scope.ServiceProvider.GetRequiredService<IOrganizationRepository>();
        var teamRepo = scope.ServiceProvider.GetRequiredService<ITeamRepository>();
        var projectRepo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var teamMemberRepo = scope.ServiceProvider.GetRequiredService<ITeamMemberRepository>();
        var ticketRepo = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        var ticketService = scope.ServiceProvider.GetRequiredService<ITicketService>();

        var slugSuffix = Guid.NewGuid().ToString("N")[..8];

        var org = new Organization
        {
            Slug = "phase2-org-" + slugSuffix,
            Name = "Phase 2 Org",
            CreatedBy = adminId,
        };
        await orgRepo.CreateAsync(org);

        var sourceTeam = new Team
        {
            OrganizationId = org.Id,
            Slug = "src-" + slugSuffix,
            Name = "Source",
            CreatedBy = adminId,
        };
        await teamRepo.CreateAsync(sourceTeam);

        var destTeam = new Team
        {
            OrganizationId = org.Id,
            Slug = "dst-" + slugSuffix,
            Name = "Destination",
            CreatedBy = adminId,
        };
        await teamRepo.CreateAsync(destTeam);

        var project = new Project
        {
            TeamId = sourceTeam.Id,
            Slug = "proj-" + slugSuffix,
            Name = "Project",
            CreatedBy = adminId,
        };
        await projectRepo.CreateAsync(project);

        await teamMemberRepo.AddAsync(new TeamMember
        {
            UserId = managerId,
            TeamId = sourceTeam.Id,
            Role = TeamMemberRole.Manager,
            AddedBy = adminId,
        });
        await teamMemberRepo.AddAsync(new TeamMember
        {
            UserId = memberId,
            TeamId = sourceTeam.Id,
            Role = TeamMemberRole.Member,
            AddedBy = adminId,
        });
        await teamMemberRepo.AddAsync(new TeamMember
        {
            UserId = viewerId,
            TeamId = sourceTeam.Id,
            Role = TeamMemberRole.Viewer,
            AddedBy = adminId,
        });

        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Title = "Phase 2 acceptance ticket",
            Description = "Drives the Phase 2.7 / 2.8 route → claim → audit loop.",
            Status = TicketStatus.Open,
            Priority = TicketPriority.Medium,
            ProjectId = project.Id,
            TeamId = sourceTeam.Id,
            ReporterId = adminId,
            AssigneeId = null,
            DateCreated = now,
            DateUpdated = now,
        };
        var ticketId = await ticketRepo.CreateAsync(ticket);

        // -------------------- Act: sign in over HTTP --------------------
        using var client = await SignInAsync(AdminEmail, AdminPassword);

        // -------------------- Act: route + assign --------------------
        var routed = await ticketService.RouteTicketAsync(adminId, ticketId, destTeam.Id);
        routed.Should().BeTrue("system_admin is allowed to route any ticket per §5.2");

        var assigned = await ticketService.AssignTicketAsync(adminId, ticketId, managerId);
        assigned.Should().BeTrue("system_admin is allowed to assign any ticket per §5.3");

        // -------------------- Assert: persisted state --------------------
        await using (var connection = new NpgsqlConnection(_factory.ConnectionString))
        {
            await connection.OpenAsync();

            var teamIdAfter = await connection.QuerySingleAsync<Guid>(
                "SELECT team_id FROM tickets WHERE id = @Id", new { Id = ticketId });
            teamIdAfter.Should().Be(destTeam.Id);

            var assigneeAfter = await connection.QuerySingleAsync<Guid?>(
                "SELECT assignee_id FROM tickets WHERE id = @Id", new { Id = ticketId });
            assigneeAfter.Should().Be(managerId);

            var routedRows = await connection.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM audit_events
                  WHERE event_type = 'ticket_routed'
                    AND actor_user_id = @ActorId
                    AND target = @Target",
                new { ActorId = adminId, Target = ticketId.ToString(CultureInfo.InvariantCulture) });
            routedRows.Should().Be(1, "the route emits exactly one ticket_routed audit row per §5.4");

            var assignedRows = await connection.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM audit_events
                  WHERE event_type = 'ticket_assigned'
                    AND actor_user_id = @ActorId
                    AND target = @Target",
                new { ActorId = adminId, Target = ticketId.ToString(CultureInfo.InvariantCulture) });
            assignedRows.Should().Be(1, "the assign emits exactly one ticket_assigned audit row per §5.4");
        }

        // -------------------- Assert: admin audit feed renders --------------------
        // The page itself is server-rendered Blazor InteractiveServer; the row
        // contents are fetched after the initial response streams (in
        // OnInitializedAsync), so HTML scraping isn't deterministic.
        //
        // Phase 4.6 step 18 added [Authorize(Policy = RequireMfa)] to every
        // /admin/* page. The seeded admin in this test has no MFA enrolment,
        // so the MfaSetupRedirectMiddlewareResultHandler now diverts the GET
        // to /account/mfa/setup with a 302. Asserting on the redirect target
        // proves both legs of the new wiring: the policy denied (admin without
        // amr=mfa) AND the redirect handler converted the deny into a setup
        // bounce rather than a flat 403.
        var auditFeedResponse = await client.GetAsync("/admin/audit");
        auditFeedResponse.StatusCode.Should().Be(HttpStatusCode.Found,
            because: "Phase 4.6 step 16 routes the seeded admin (no MFA enrolled) through the RequireMfa setup redirect.");
        auditFeedResponse.Headers.Location?.ToString().Should().StartWith(
            "/account/mfa/setup",
            because: "the RequireMfa redirect handler bounces admins without amr=mfa to enrolment.");
    }

    // -------------------- helpers --------------------

    private static async Task<Guid> SeedUserAsync(IServiceProvider rootServices, string email, string password)
    {
        await using var scope = rootServices.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();

        var existing = await userManager.FindByEmailAsync(email);
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

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Phase 2 acceptance fixture failed to seed user "
                + email
                + ": "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
        }

        return user.Id;
    }

    private async Task<Guid> SeedSystemAdminAsync(IServiceProvider rootServices, string email, string password)
    {
        var userId = await SeedUserAsync(rootServices, email, password);

        // The Phase 2.7 system_admin column is a bare flag on users; flipping it
        // via direct SQL avoids leaning on Identity for a column it doesn't model.
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE users SET system_admin = true WHERE id = @Id",
            new { Id = userId });

        return userId;
    }

    /// <summary>
    /// Drives the cookie login flow at <c>/login</c> the same way the production
    /// UI does (mirrors <see cref="Phase1AcceptanceTests"/>) and returns a client
    /// whose handler now carries the auth cookie for follow-up GETs.
    /// </summary>
    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var loginPage = await client.GetAsync("/login");
        loginPage.EnsureSuccessStatusCode();
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(html);

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("email", email),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var loginResponse = await client.PostAsync("/account/login", form);
        loginResponse.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Found },
            "valid credentials must mint the auth cookie and 302 to '/'");

        return client;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
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
                "Antiforgery token hidden input not found in rendered /login HTML.");
        }

        return match.Groups["token"].Value;
    }
}

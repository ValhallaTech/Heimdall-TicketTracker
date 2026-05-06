using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Heimdall.BLL.Authorization;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Components.Pages.Teams;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Heimdall.Web.Tests.Components.Pages.Teams;

/// <summary>
/// bUnit tests for the per-team queue page (<c>/teams/{Slug}/queue</c>) introduced in
/// Phase 2.8 step 23 (<c>docs/proposals/team-collaboration.md</c> §5.1, §7).
/// </summary>
public class QueueTests : BunitContext
{
    private const string TeamSlug = "alpha";
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherMemberId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Loose);
    private readonly Mock<ITicketService> _tickets = new(MockBehavior.Loose);
    private readonly Mock<IPermissionService> _permissions = new(MockBehavior.Loose);
    private readonly Mock<ITeamMemberRepository> _members = new(MockBehavior.Loose);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);

    public QueueTests()
    {
        Services.AddSingleton(_teams.Object);
        Services.AddSingleton(_tickets.Object);
        Services.AddSingleton(_permissions.Object);
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(_userLookup.Object);

        // Cascading auth state with a parseable NameIdentifier — the page redirects
        // to /access-denied when this is missing/unparseable, which would mask the
        // behaviours under test.
        var auth = AddAuthorization();
        auth.SetAuthorized("test-user");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, ActorId.ToString()));

        // Sensible defaults for the "happy path" branches; overridden per test as needed.
        _teams.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<Team>());
        _members.Setup(r => r.GetByParentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<TeamMember>());
        _userLookup.Setup(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Guid id, CancellationToken _) => new UserSummary(id, $"{id:N}@example.test"));
    }

    private void SetupTeamFound() =>
        _teams.Setup(r => r.GetBySlugAsync(TeamSlug, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Team { Id = TeamId, Slug = TeamSlug, Name = "Alpha" });

    private void SetupPermission(bool view, bool route = false, bool claim = false, bool assign = false)
    {
        _permissions.Setup(p => p.CanViewTeamQueueAsync(ActorId, TeamId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(view);
        _permissions.Setup(p => p.CanRouteTicketAsync(ActorId, It.IsAny<Ticket>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(route);
        // CanAssignTicketAsync is invoked twice per ticket (claim probe with target == actor,
        // assign probe with a non-self teammate). Match both via It.IsAny<Guid>().
        _permissions.Setup(p => p.CanAssignTicketAsync(ActorId, It.IsAny<Ticket>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid _, Ticket _, Guid target, CancellationToken _) => target == ActorId ? claim : assign);
    }

    private static List<TicketDto> MakeTickets(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new TicketDto
            {
                Id = i,
                Title = $"Ticket {i}",
                Description = "d",
                Status = TicketStatus.Open,
                Priority = TicketPriority.Medium,
                TeamId = TeamId,
                ReporterId = ActorId,
                AssigneeId = null,
                DateCreated = DateTimeOffset.UtcNow,
                DateUpdated = DateTimeOffset.UtcNow,
            })
            .ToList();

    [Fact]
    public void Render_Should_RenderNotFoundAlert_When_TeamSlugDoesNotResolve()
    {
        _teams.Setup(r => r.GetBySlugAsync(TeamSlug, It.IsAny<CancellationToken>()))
              .ReturnsAsync((Team?)null);

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-warning");
            cut.Markup.Should().Contain("No team with slug");
            cut.Markup.Should().Contain($"<code>{TeamSlug}</code>");
        });

        _tickets.Verify(t => t.GetByTeamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Render_Should_NavigateToAccessDeniedAndSkipFetch_When_PermissionDenied()
    {
        SetupTeamFound();
        SetupPermission(view: false);

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));

        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/access-denied"));

        _tickets.Verify(t => t.GetByTeamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Render_Should_RenderOneRowPerTicket_When_PermittedAndQueueNonEmpty()
    {
        SetupTeamFound();
        SetupPermission(view: true);
        _tickets.Setup(t => t.GetByTeamAsync(TeamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeTickets(3));

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(3));
    }

    [Fact]
    public void Render_Should_RenderRouteClaimAndAssignButtons_When_AllPermissionsGranted()
    {
        // A non-self teammate is required for the "assign-to-anyone" probe target to be
        // non-empty; otherwise the page short-circuits the assign flag to false regardless
        // of what the permission service returns.
        SetupTeamFound();
        SetupPermission(view: true, route: true, claim: true, assign: true);
        _members.Setup(r => r.GetByParentAsync(TeamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new TeamMember { TeamId = TeamId, UserId = OtherMemberId, Role = TeamMemberRole.Member } });
        _tickets.Setup(t => t.GetByTeamAsync(TeamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeTickets(2));

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button.btn-outline-primary").Count.Should().Be(2);  // Route × rows
            cut.FindAll("button.btn-outline-success").Count.Should().Be(2);  // Claim × rows
            cut.FindAll("button.btn-outline-secondary[title^='Assign']").Count.Should().Be(2); // Assign × rows
        });
    }

    [Fact]
    public void Render_Should_RenderNoActionButtons_When_AllPermissionsDenied()
    {
        SetupTeamFound();
        SetupPermission(view: true, route: false, claim: false, assign: false);
        _tickets.Setup(t => t.GetByTeamAsync(TeamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeTickets(2));

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Count.Should().Be(2);
            cut.FindAll("button.btn-outline-primary").Count.Should().Be(0);
            cut.FindAll("button.btn-outline-success").Count.Should().Be(0);
            cut.FindAll("button.btn-outline-secondary[title^='Assign']").Count.Should().Be(0);
        });
    }

    [Fact]
    public void ClaimAsync_Should_InvokeServiceOnce_When_ClaimButtonClicked()
    {
        SetupTeamFound();
        SetupPermission(view: true, claim: true);
        _tickets.Setup(t => t.GetByTeamAsync(TeamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeTickets(1));
        _tickets.Setup(t => t.ClaimTicketAsync(ActorId, 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));
        cut.WaitForAssertion(() => cut.Find("button.btn-outline-success").Should().NotBeNull());

        cut.Find("button.btn-outline-success").Click();

        cut.WaitForAssertion(() =>
            _tickets.Verify(s => s.ClaimTicketAsync(ActorId, 1, It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void ClaimAsync_Should_RenderInlineAlertAndKeepCircuit_When_UnauthorizedAccessThrown()
    {
        SetupTeamFound();
        SetupPermission(view: true, claim: true);
        _tickets.Setup(t => t.GetByTeamAsync(TeamId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeTickets(1));
        _tickets.Setup(t => t.ClaimTicketAsync(ActorId, 1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UnauthorizedAccessException("nope"));

        var cut = Render<Queue>(p => p.Add(c => c.Slug, TeamSlug));
        cut.WaitForAssertion(() => cut.Find("button.btn-outline-success").Should().NotBeNull());

        // Should not throw / tear down the circuit.
        cut.Find("button.btn-outline-success").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-danger");
            cut.Markup.Should().Contain("permission to claim");
            // Page is still alive: the table is still rendered.
            cut.FindAll("tbody tr").Count.Should().Be(1);
        });
    }
}

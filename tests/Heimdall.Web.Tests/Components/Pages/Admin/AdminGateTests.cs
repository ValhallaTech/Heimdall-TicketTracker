using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Heimdall.Core.Auditing;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Heimdall.Web.Tests.Components.Pages.Admin;

/// <summary>
/// bUnit tests for the inline <see cref="AdminGate"/> applied to every
/// <c>/admin/*</c> page (Phase 2.8 step 25 of
/// <c>docs/proposals/team-collaboration.md</c> §7). Each admin page gets two
/// cases: deny → redirect to <c>/access-denied</c>; allow → page-specific
/// content renders.
/// </summary>
public class AdminGateTests : BunitContext
{
    private static readonly Guid AdminId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);
    private readonly Mock<IOrganizationRepository> _orgs = new(MockBehavior.Loose);
    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Loose);
    private readonly Mock<IProjectRepository> _projects = new(MockBehavior.Loose);
    private readonly Mock<ITeamMemberRepository> _teamMembers = new(MockBehavior.Loose);
    private readonly Mock<IAuditEventReader> _audit = new(MockBehavior.Loose);

    public AdminGateTests()
    {
        Services.AddSingleton(_userLookup.Object);
        Services.AddSingleton(_orgs.Object);
        Services.AddSingleton(_teams.Object);
        Services.AddSingleton(_projects.Object);
        Services.AddSingleton(_teamMembers.Object);
        Services.AddSingleton(_audit.Object);

        // Backing data — empty so each page exits its loading branch with empty content
        // and we can assert on the page-specific heading without seeding hierarchy data.
        _orgs.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Organization>());
        _teams.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Team>());
        _teams.Setup(r => r.GetByOrganizationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<Team>());
        _projects.Setup(r => r.GetByTeamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Array.Empty<Project>());
        _teamMembers.Setup(r => r.GetByParentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<TeamMember>());
        _audit.Setup(r => r.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<AuditEventRecord>());
    }

    private void SeedAuth(bool isSystemAdmin)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("admin-user");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, AdminId.ToString()));
        _userLookup.Setup(u => u.IsSystemAdminAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(isSystemAdmin);
    }

    private void AssertRedirectedToAccessDenied()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().EndWith("/access-denied");
    }

    // ---------- Hierarchy ----------
    [Fact]
    public void HierarchyRender_Should_RedirectToAccessDenied_When_NotSystemAdmin()
    {
        SeedAuth(isSystemAdmin: false);

        var cut = Render<Hierarchy>();

        cut.WaitForAssertion(AssertRedirectedToAccessDenied);
    }

    [Fact]
    public void HierarchyRender_Should_RenderHeading_When_SystemAdmin()
    {
        SeedAuth(isSystemAdmin: true);

        var cut = Render<Hierarchy>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(">Hierarchy<"));
    }

    // ---------- Memberships ----------
    [Fact]
    public void MembershipsRender_Should_RedirectToAccessDenied_When_NotSystemAdmin()
    {
        SeedAuth(isSystemAdmin: false);

        var cut = Render<Memberships>();

        cut.WaitForAssertion(AssertRedirectedToAccessDenied);
    }

    [Fact]
    public void MembershipsRender_Should_RenderHeading_When_SystemAdmin()
    {
        SeedAuth(isSystemAdmin: true);

        var cut = Render<Memberships>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Team memberships"));
    }

    // ---------- Queues ----------
    [Fact]
    public void QueuesRender_Should_RedirectToAccessDenied_When_NotSystemAdmin()
    {
        SeedAuth(isSystemAdmin: false);

        var cut = Render<Queues>();

        cut.WaitForAssertion(AssertRedirectedToAccessDenied);
    }

    [Fact]
    public void QueuesRender_Should_RenderHeading_When_SystemAdmin()
    {
        SeedAuth(isSystemAdmin: true);

        var cut = Render<Queues>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Team queues"));
    }

    // ---------- AuditFeed ----------
    [Fact]
    public void AuditFeedRender_Should_RedirectToAccessDenied_When_NotSystemAdmin()
    {
        SeedAuth(isSystemAdmin: false);

        var cut = Render<AuditFeed>();

        cut.WaitForAssertion(AssertRedirectedToAccessDenied);
    }

    [Fact]
    public void AuditFeedRender_Should_RenderHeading_When_SystemAdmin()
    {
        SeedAuth(isSystemAdmin: true);

        var cut = Render<AuditFeed>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Audit feed"));
    }
}

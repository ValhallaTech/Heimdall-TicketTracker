using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Heimdall.BLL.Services;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Heimdall.Web.Tests.Components.Pages.Admin;

/// <summary>
/// bUnit tests for the Phase 3.6 step 11 admin membership management page
/// (<c>/admin/memberships</c>). Covers tab switching, debounced email
/// search, the add / remove flows per scope, and the friendly error /
/// status surfaces.
/// </summary>
public class MembershipsTests : BunitContext
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TeamId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid OrgMemberUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TeamMemberUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ProjectMemberUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PickUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly Mock<IOrganizationRepository> _orgs = new(MockBehavior.Loose);
    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Loose);
    private readonly Mock<IProjectRepository> _projects = new(MockBehavior.Loose);
    private readonly Mock<IOrganizationMemberRepository> _orgMembers = new(MockBehavior.Loose);
    private readonly Mock<ITeamMemberRepository> _teamMembers = new(MockBehavior.Loose);
    private readonly Mock<IProjectMemberRepository> _projectMembers = new(MockBehavior.Loose);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);
    private readonly Mock<IMembershipAdminService> _admin = new(MockBehavior.Loose);

    public MembershipsTests()
    {
        Services.AddSingleton(_orgs.Object);
        Services.AddSingleton(_teams.Object);
        Services.AddSingleton(_projects.Object);
        Services.AddSingleton(_orgMembers.Object);
        Services.AddSingleton(_teamMembers.Object);
        Services.AddSingleton(_projectMembers.Object);
        Services.AddSingleton(_userLookup.Object);
        Services.AddSingleton(_admin.Object);

        var auth = AddAuthorization();
        auth.SetAuthorized("test-admin");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, ActorId.ToString()));

        // Hierarchy load: one org → one team → one project. These are the
        // only three scope ids the tests interact with.
        _orgs.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new[] { new Organization { Id = OrgId, Slug = "acme", Name = "Acme" } });
        _teams.Setup(r => r.GetByOrganizationAsync(OrgId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { new Team { Id = TeamId, OrganizationId = OrgId, Slug = "core", Name = "Core" } });
        _projects.Setup(r => r.GetByTeamAsync(TeamId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new[] { new Project { Id = ProjectId, TeamId = TeamId, Slug = "web", Name = "Web" } });

        // Roster defaults — one member per scope so the table renders a row.
        _orgMembers.Setup(r => r.GetByParentAsync(OrgId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new OrganizationMember { OrganizationId = OrgId, UserId = OrgMemberUserId, Role = "member" } });
        _teamMembers.Setup(r => r.GetByParentAsync(TeamId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { new TeamMember { TeamId = TeamId, UserId = TeamMemberUserId, Role = TeamMemberRole.Manager } });
        _projectMembers.Setup(r => r.GetByParentAsync(ProjectId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new[] { new ProjectMember { ProjectId = ProjectId, UserId = ProjectMemberUserId, Role = "viewer" } });

        _userLookup.Setup(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Guid id, CancellationToken _) => new UserSummary(id, $"{id:N}@example.test"));
        _userLookup.Setup(u => u.SearchByEmailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<UserSummary>());
    }

    [Fact]
    public void Should_RenderOrgTabActiveByDefault_When_PageInitializes()
    {
        var cut = Render<Memberships>();

        cut.WaitForAssertion(() =>
        {
            var orgTab = cut.FindAll("button.nav-link").First(b => b.TextContent.Contains("Organizations"));
            orgTab.ClassList.Should().Contain("active");
            cut.Markup.Should().Contain("Organization"); // ScopeLabel
        });
    }

    [Fact]
    public void Should_NavigateToAccessDenied_When_NameIdentifierClaimMissing()
    {
        // Re-create the auth context with no NameIdentifier claim so the page's
        // actor-resolution branch redirects to /access-denied.
        var auth = AddAuthorization();
        auth.SetAuthorized("test-admin");
        auth.SetClaims(); // no claims

        Render<Memberships>();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().EndWith("/access-denied");
    }

    [Fact]
    public void Should_SwitchToTeamTabAndRenderTeamScopeOptions_When_TeamTabClicked()
    {
        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        var teamButton = cut.FindAll("button.nav-link").First(b => b.TextContent.Contains("Teams"));
        teamButton.Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button.nav-link")
                .First(b => b.TextContent.Contains("Teams"))
                .ClassList.Should().Contain("active");
            cut.Markup.Should().Contain("Core (core)");
        });
    }

    [Fact]
    public void Should_LoadOrgMembers_When_ScopeSelectedAndLoadClicked()
    {
        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Count.Should().Be(1);
            cut.Markup.Should().Contain(OrgMemberUserId.ToString("N").Substring(0, 6));
        });
    }

    [Fact]
    public void Should_RenderNoMembers_When_RosterEmpty()
    {
        _orgMembers.Setup(r => r.GetByParentAsync(OrgId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<OrganizationMember>());

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No members."));
    }

    [Fact]
    public void Should_SurfaceLoadError_When_RepositoryThrows()
    {
        _orgMembers.Setup(r => r.GetByParentAsync(OrgId, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("boom"));

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-danger");
            cut.Markup.Should().Contain("Failed to load members.");
        });
    }

    [Fact]
    public void Should_DebouncedSearchInvokeUserLookupWithTrimmedFragment_When_EmailTyped()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("alice", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "alice@example.test") });

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());

        cut.Find("#add-email").Input("alice");

        cut.WaitForAssertion(
            () =>
            {
                _userLookup.Verify(
                    u => u.SearchByEmailAsync("alice", 25, It.IsAny<CancellationToken>()),
                    Times.AtLeastOnce);
                cut.Markup.Should().Contain("alice@example.test");
            },
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Should_RenderNoMatches_When_SearchResultsEmpty()
    {
        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());

        cut.Find("#add-email").Input("zzz");

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("No matches."),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Should_CallAddOrgMember_When_AddOnOrgTab()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("alice", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "alice@example.test") });

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());

        cut.Find("#add-email").Input("alice");
        cut.WaitForAssertion(
            () => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pick").Should().BeTrue(),
            TimeSpan.FromSeconds(2));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Pick").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Add ")).Click();

        cut.WaitForAssertion(() =>
            _admin.Verify(
                a => a.AddOrgMemberAsync(OrgId, PickUserId, "member", ActorId, It.IsAny<CancellationToken>()),
                Times.Once));
    }

    [Fact]
    public void Should_CallAddTeamMemberWithMappedRole_When_AddOnTeamTab()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("bob", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "bob@example.test") });

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.FindAll("button.nav-link").First(b => b.TextContent.Contains("Teams")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Core (core)"));

        cut.Find("#scope-select").Change(TeamId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());

        cut.Find("#add-email").Input("bob");
        cut.WaitForAssertion(
            () => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pick").Should().BeTrue(),
            TimeSpan.FromSeconds(2));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Pick").Click();

        // Default role on team tab is "member" → TeamMemberRole.Member.
        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Add ")).Click();

        cut.WaitForAssertion(() =>
            _admin.Verify(
                a => a.AddTeamMemberAsync(TeamId, PickUserId, TeamMemberRole.Member, ActorId, It.IsAny<CancellationToken>()),
                Times.Once));
    }

    [Fact]
    public void Should_CallAddProjectMember_When_AddOnProjectTab()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("carol", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "carol@example.test") });

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.FindAll("button.nav-link").First(b => b.TextContent.Contains("Projects")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Web (web)"));

        cut.Find("#scope-select").Change(ProjectId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());

        cut.Find("#add-email").Input("carol");
        cut.WaitForAssertion(
            () => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pick").Should().BeTrue(),
            TimeSpan.FromSeconds(2));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Pick").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Add ")).Click();

        cut.WaitForAssertion(() =>
            _admin.Verify(
                a => a.AddProjectMemberAsync(ProjectId, PickUserId, "member", ActorId, It.IsAny<CancellationToken>()),
                Times.Once));
    }

    [Fact]
    public void Should_SurfaceFriendlyDuplicateError_When_AddRaisesUniqueViolation()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("alice", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "alice@example.test") });
        _admin.Setup(a => a.AddOrgMemberAsync(OrgId, PickUserId, "member", ActorId, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new Npgsql.PostgresException("duplicate", "ERROR", "ERROR", "23505"));

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());
        cut.Find("#add-email").Input("alice");
        cut.WaitForAssertion(
            () => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pick").Should().BeTrue(),
            TimeSpan.FromSeconds(2));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Pick").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Add ")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-danger");
            cut.Markup.Should().Contain("already a member");
        });
    }

    [Fact]
    public void Should_SurfaceArgumentExceptionMessage_When_AddRaisesArgumentException()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("alice", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "alice@example.test") });
        _admin.Setup(a => a.AddOrgMemberAsync(OrgId, PickUserId, "member", ActorId, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new ArgumentException("invalid role: xyz"));

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());
        cut.Find("#add-email").Input("alice");
        cut.WaitForAssertion(
            () => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pick").Should().BeTrue(),
            TimeSpan.FromSeconds(2));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Pick").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Add ")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-danger");
            cut.Markup.Should().Contain("invalid role: xyz");
        });
    }

    [Fact]
    public void Should_SurfaceGenericFailure_When_AddThrowsUnexpected()
    {
        _userLookup.Setup(u => u.SearchByEmailAsync("alice", 25, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { new UserSummary(PickUserId, "alice@example.test") });
        _admin.Setup(a => a.AddOrgMemberAsync(OrgId, PickUserId, "member", ActorId, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.Find("#add-email").Should().NotBeNull());
        cut.Find("#add-email").Input("alice");
        cut.WaitForAssertion(
            () => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pick").Should().BeTrue(),
            TimeSpan.FromSeconds(2));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Pick").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Add ")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-danger");
            cut.Markup.Should().Contain("Failed to add member.");
        });
    }

    [Fact]
    public void Should_OpenConfirmModalAndCallRemoveOrgMember_When_RemoveConfirmed()
    {
        _admin.Setup(a => a.RemoveOrgMemberAsync(OrgId, OrgMemberUserId, ActorId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));

        cut.FindAll("button.btn-outline-danger").First().Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm removal"));

        cut.FindAll("button.btn-danger").First().Click();

        cut.WaitForAssertion(() =>
        {
            _admin.Verify(
                a => a.RemoveOrgMemberAsync(OrgId, OrgMemberUserId, ActorId, It.IsAny<CancellationToken>()),
                Times.Once);
            cut.Markup.Should().Contain("Member removed.");
            // Modal should be dismissed after confirm.
            cut.Markup.Should().NotContain("Confirm removal");
        });
    }

    [Fact]
    public void Should_DismissModal_When_CancelClicked()
    {
        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));

        cut.FindAll("button.btn-outline-danger").First().Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm removal"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Confirm removal"));
        _admin.Verify(
            a => a.RemoveOrgMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Should_CallRemoveTeamMember_When_RemoveOnTeamTab()
    {
        _admin.Setup(a => a.RemoveTeamMemberAsync(TeamId, TeamMemberUserId, ActorId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.FindAll("button.nav-link").First(b => b.TextContent.Contains("Teams")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Core (core)"));
        cut.Find("#scope-select").Change(TeamId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));

        cut.FindAll("button.btn-outline-danger").First().Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm removal"));
        cut.FindAll("button.btn-danger").First().Click();

        cut.WaitForAssertion(() =>
        {
            _admin.Verify(
                a => a.RemoveTeamMemberAsync(TeamId, TeamMemberUserId, ActorId, It.IsAny<CancellationToken>()),
                Times.Once);
            // RemoveTeamMemberAsync returned false → "Already removed." status.
            cut.Markup.Should().Contain("Already removed.");
        });
    }

    [Fact]
    public void Should_CallRemoveProjectMember_When_RemoveOnProjectTab()
    {
        _admin.Setup(a => a.RemoveProjectMemberAsync(ProjectId, ProjectMemberUserId, ActorId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());

        cut.FindAll("button.nav-link").First(b => b.TextContent.Contains("Projects")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Web (web)"));
        cut.Find("#scope-select").Change(ProjectId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));

        cut.FindAll("button.btn-outline-danger").First().Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm removal"));
        cut.FindAll("button.btn-danger").First().Click();

        cut.WaitForAssertion(() =>
            _admin.Verify(
                a => a.RemoveProjectMemberAsync(ProjectId, ProjectMemberUserId, ActorId, It.IsAny<CancellationToken>()),
                Times.Once));
    }

    [Fact]
    public void Should_SurfaceRemoveError_When_RemoveThrows()
    {
        _admin.Setup(a => a.RemoveOrgMemberAsync(OrgId, OrgMemberUserId, ActorId, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        var cut = Render<Memberships>();
        cut.WaitForAssertion(() => cut.Find("#scope-select").Should().NotBeNull());
        cut.Find("#scope-select").Change(OrgId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Contains("Load members")).Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));

        cut.FindAll("button.btn-outline-danger").First().Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm removal"));
        cut.FindAll("button.btn-danger").First().Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alert-danger");
            cut.Markup.Should().Contain("Failed to remove member.");
        });
    }
}

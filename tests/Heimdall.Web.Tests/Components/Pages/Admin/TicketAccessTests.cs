using Bunit;
using FluentAssertions;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Components.Pages.Admin;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Heimdall.Web.Tests.Components.Pages.Admin;

/// <summary>
/// bUnit tests for the Phase 3.6 step 11 admin "Ticket access" inspect page
/// (<c>/admin/access/ticket/{TicketId:int}</c>). Validates load-time fetches,
/// inspect-button driven ListUsers/Expand calls, the truncation badge, and
/// the recursive userset-tree render.
/// </summary>
public class TicketAccessTests : BunitContext
{
    private const int TicketId = 42;
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TeamId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid OrgId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private readonly Mock<IOpenFgaAuthorizationService> _fga = new(MockBehavior.Loose);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);
    private readonly Mock<ITicketRepository> _tickets = new(MockBehavior.Loose);
    private readonly Mock<IProjectRepository> _projects = new(MockBehavior.Loose);
    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Loose);
    private readonly Mock<IOrganizationRepository> _orgs = new(MockBehavior.Loose);

    public TicketAccessTests()
    {
        Services.AddSingleton(_fga.Object);
        Services.AddSingleton(_userLookup.Object);
        Services.AddSingleton(_tickets.Object);
        Services.AddSingleton(_projects.Object);
        Services.AddSingleton(_teams.Object);
        Services.AddSingleton(_orgs.Object);

        _tickets.Setup(r => r.GetByIdAsync(TicketId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Ticket
                {
                    Id = TicketId,
                    Title = "Sample ticket",
                    ProjectId = ProjectId,
                    TeamId = TeamId,
                    ReporterId = Guid.NewGuid(),
                });
        _projects.Setup(r => r.GetByIdAsync(ProjectId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new Project { Id = ProjectId, TeamId = TeamId, Slug = "p", Name = "P-Project" });
        _teams.Setup(r => r.GetByIdAsync(TeamId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Team { Id = TeamId, OrganizationId = OrgId, Slug = "t", Name = "T-Team" });
        _orgs.Setup(r => r.GetByIdAsync(OrgId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Organization { Id = OrgId, Slug = "o", Name = "O-Org" });
        _userLookup.Setup(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Guid id, CancellationToken _) => new UserSummary(id, $"{id:N}@example.test"));

        _fga.Setup(f => f.ListUsersAsync(It.IsAny<FgaListUsersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _fga.Setup(f => f.ExpandAsync(It.IsAny<FgaExpandRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FgaExpandResult(null));
    }

    [Fact]
    public void Should_RenderTicketNotFound_When_TicketRepoReturnsNull()
    {
        _tickets.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Ticket?)null);

        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, 99));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ticket not found."));
    }

    [Fact]
    public void Should_RenderTicketMetadata_When_TicketLoaded()
    {
        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Sample ticket");
            cut.Markup.Should().Contain("P-Project");
            cut.Markup.Should().Contain("T-Team");
            cut.Markup.Should().Contain("O-Org");
        });
    }

    [Fact]
    public void Should_CallListUsersAndExpand_When_InspectButtonClicked()
    {
        var userId = Guid.NewGuid();
        _fga.Setup(f => f.ListUsersAsync(
                It.Is<FgaListUsersRequest>(r => r.ObjectType == "ticket" && r.ObjectId == TicketId.ToString() && r.Relation == "view"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { userId.ToString() });
        _fga.Setup(f => f.ExpandAsync(
                It.Is<FgaExpandRequest>(r => r.ObjectType == "ticket" && r.ObjectId == TicketId.ToString() && r.Relation == "view"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FgaExpandResult(new FgaExpandNode("ticket:42#view",
                new FgaExpandLeaf(new[] { $"user:{userId}" }, null, null), null, null, null)));

        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").Should().NotBeNull());

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            _fga.Verify(f => f.ListUsersAsync(It.IsAny<FgaListUsersRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            _fga.Verify(f => f.ExpandAsync(It.IsAny<FgaExpandRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            // The user-lookup stub renders the resolved user as
            // "{id:N}@example.test" — assert the full email shows up in the
            // user list (no magic-number substring).
            cut.Markup.Should().Contain($"{userId:N}@example.test");
            cut.Markup.Should().NotContain("Truncated");
        });
    }

    [Fact]
    public void Should_RenderTruncatedBadge_When_ListUsersExceedsCap()
    {
        // 51 user ids → past the 50 row UI cap → "Truncated" badge renders.
        var ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid().ToString()).ToArray();
        _fga.Setup(f => f.ListUsersAsync(It.IsAny<FgaListUsersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").Should().NotBeNull());
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Truncated");
            cut.FindAll("ul.list-group .list-group-item").Count.Should().Be(50);
        });
    }

    [Fact]
    public void Should_RenderEmptyUserCard_When_ListUsersReturnsEmpty()
    {
        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").Should().NotBeNull());
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No users."));
    }

    [Fact]
    public void Should_RenderUsersetTree_When_ExpandReturnsRichTree()
    {
        // Hand-build a userset tree exercising every render branch:
        //   union of: leaf-users, computed-userset, tuple-to-userset,
        //   plus a difference and an intersection nested below.
        var leafUsers = new FgaExpandNode(
            "leaf-users",
            new FgaExpandLeaf(new[] { "user:11111111-1111-1111-1111-111111111111" }, null, null),
            null, null, null);
        var leafComputed = new FgaExpandNode(
            "leaf-computed",
            new FgaExpandLeaf(Array.Empty<string>(), "project:abc#admin", null),
            null, null, null);
        var leafTtu = new FgaExpandNode(
            "leaf-ttu",
            new FgaExpandLeaf(
                Array.Empty<string>(),
                null,
                new FgaExpandTupleToUserset("parent_project", new[] { "view" })),
            null, null, null);
        var intersectionNode = new FgaExpandNode(
            "intersection",
            null,
            null,
            new[] { leafUsers, leafComputed },
            null);
        var differenceNode = new FgaExpandNode(
            "difference",
            null,
            null,
            null,
            new FgaExpandDifference(leafUsers, leafComputed));
        var root = new FgaExpandNode(
            string.Empty, // empty root name → "(root)" label branch
            null,
            new[] { leafUsers, leafComputed, leafTtu, intersectionNode, differenceNode },
            null,
            null);

        _fga.Setup(f => f.ExpandAsync(It.IsAny<FgaExpandRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FgaExpandResult(root));

        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").Should().NotBeNull());
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            // Root rendered with "(root)" placeholder.
            cut.Markup.Should().Contain("(root)");
            // Union / intersection / difference badges all surfaced.
            cut.Markup.Should().Contain("union");
            cut.Markup.Should().Contain("intersection");
            cut.Markup.Should().Contain("difference");
            // Leaf payloads rendered.
            cut.Markup.Should().Contain("user:11111111-1111-1111-1111-111111111111");
            cut.Markup.Should().Contain("project:abc#admin");
            cut.Markup.Should().Contain("parent_project");
        });
    }

    [Fact]
    public void Should_RenderNoTreeMessage_When_ExpandRootIsNull()
    {
        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").Should().NotBeNull());

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No tree returned."));
    }

    [Fact]
    public void Should_StayAliveWithoutTreeOrUsers_When_AdapterThrows()
    {
        _fga.Setup(f => f.ListUsersAsync(It.IsAny<FgaListUsersRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport failure"));

        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("button.btn-primary").Should().NotBeNull());

        // Should not throw / kill the page.
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            // _loaded stays false → the user / tree cards never render.
            cut.Markup.Should().NotContain("Userset tree");
            cut.Markup.Should().Contain("Sample ticket");
        });
    }

    [Fact]
    public void Should_PassRelationToBothAdapterCalls_When_NonDefaultRelationSelected()
    {
        var cut = Render<TicketAccess>(p => p.Add(c => c.TicketId, TicketId));
        cut.WaitForAssertion(() => cut.Find("#relation").Should().NotBeNull());

        cut.Find("#relation").Change("edit");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            _fga.Verify(
                f => f.ListUsersAsync(
                    It.Is<FgaListUsersRequest>(r => r.Relation == "edit"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _fga.Verify(
                f => f.ExpandAsync(
                    It.Is<FgaExpandRequest>(r => r.Relation == "edit"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }
}

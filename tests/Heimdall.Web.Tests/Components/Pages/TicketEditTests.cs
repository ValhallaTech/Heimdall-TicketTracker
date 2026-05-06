using Bunit;
using FluentAssertions;
using Heimdall.Core.Dtos;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Heimdall.Web.Tests.Components.Pages;

public class TicketEditTests : BunitContext
{
    private static readonly Guid SeedOrganizationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SeedTeamId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid SeedProjectId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid SeedReporterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeedAssigneeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<ITicketService> _service = new(MockBehavior.Loose);
    private readonly Mock<IOrganizationRepository> _organizations = new(MockBehavior.Loose);
    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Loose);
    private readonly Mock<IProjectRepository> _projects = new(MockBehavior.Loose);
    private readonly Mock<ITeamMemberRepository> _teamMembers = new(MockBehavior.Loose);
    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Loose);

    public TicketEditTests()
    {
        // Match the runtime defaults the production DefaultHierarchyBootstrapper
        // creates (slugs heimdall / default / default), so the new-ticket form's
        // pre-populate path resolves real ids and the [NotEmptyGuid] validation
        // on TicketDto.ProjectId / TeamId passes on submit.
        _organizations
            .Setup(r => r.GetBySlugAsync("heimdall", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Id = SeedOrganizationId, Slug = "heimdall", Name = "Heimdall" });
        var seedTeam = new Team { Id = SeedTeamId, OrganizationId = SeedOrganizationId, Slug = "default", Name = "Default" };
        _teams
            .Setup(r => r.GetBySlugAsync(SeedOrganizationId, "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(seedTeam);
        _teams
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { seedTeam });
        _projects
            .Setup(r => r.GetBySlugAsync(SeedTeamId, "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = SeedProjectId, TeamId = SeedTeamId, Slug = "default", Name = "Default" });
        _projects
            .Setup(r => r.GetByTeamAsync(SeedTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Project { Id = SeedProjectId, TeamId = SeedTeamId, Slug = "default", Name = "Default" } });

        // Phase 2.8 step 24: TicketEdit's reporter / assignee pickers populate
        // their <option> list from team membership. Register a default member set
        // covering the well-known guids used by the existing test cases so
        // .Change("<guid>") still succeeds on the <select> elements.
        _teamMembers
            .Setup(r => r.GetByParentAsync(SeedTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TeamMember { TeamId = SeedTeamId, UserId = SeedReporterId, Role = TeamMemberRole.Member },
                new TeamMember { TeamId = SeedTeamId, UserId = SeedAssigneeId, Role = TeamMemberRole.Member },
            });
        _userLookup
            .Setup(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new UserSummary(id, id.ToString()));

        Services.AddSingleton(_service.Object);
        Services.AddSingleton(_organizations.Object);
        Services.AddSingleton(_teams.Object);
        Services.AddSingleton(_projects.Object);
        Services.AddSingleton(_teamMembers.Object);
        Services.AddSingleton(_userLookup.Object);
    }

    [Fact]
    public void Should_RenderNewTicketHeading_When_IdIsNull()
    {
        var cut = Render<TicketEdit>();

        cut.Markup.Should().Contain("New Ticket");
        cut.Find("button[type='submit']").TextContent.Should().Contain("Create Ticket");
    }

    [Fact]
    public void Should_RenderEditHeadingAndPopulateForm_When_TicketExists()
    {
        var reporter = SeedReporterId;
        var assignee = SeedAssigneeId;
        var dto = new TicketDto
        {
            Id = 42,
            Title = "Existing Title",
            Description = "Existing Desc",
            Status = TicketStatus.InProgress,
            Priority = TicketPriority.High,
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = reporter,
            AssigneeId = assignee,
        };
        _service
            .Setup(s => s.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var cut = Render<TicketEdit>(p => p.Add(c => c.Id, 42));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Edit Ticket");
            cut.Find("#title").GetAttribute("value").Should().Be("Existing Title");
            // Phase 2.8 step 24: reporter / assignee are now <select> elements,
            // populated from team membership. The current value is reflected on
            // the selected <option>.
            var reporterSelect = cut.Find("#reporter");
            var assigneeSelect = cut.Find("#assignee");
            reporterSelect.QuerySelector("option[selected]")?.GetAttribute("value")
                .Should().Be(reporter.ToString());
            assigneeSelect.QuerySelector("option[selected]")?.GetAttribute("value")
                .Should().Be(assignee.ToString());
        });
    }

    [Fact]
    public void Should_FallBackToPlaceholderTicket_When_GetByIdReturnsNull()
    {
        _service
            .Setup(s => s.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TicketDto?)null);

        var cut = Render<TicketEdit>(p => p.Add(c => c.Id, 99));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Edit Ticket");
            // Empty title indicates the placeholder fallback was used.
            cut.Find("#title").GetAttribute("value").Should().BeEmpty();
        });
    }

    [Fact]
    public void Should_CallCreateAndNavigate_When_NewFormIsSubmitted()
    {
        TicketDto? captured = null;
        _service
            .Setup(s => s.CreateAsync(It.IsAny<TicketDto>(), It.IsAny<CancellationToken>()))
            .Callback<TicketDto, CancellationToken>((d, _) => captured = d)
            .ReturnsAsync((TicketDto d, CancellationToken _) =>
            {
                d.Id = 1;
                return d;
            });

        var cut = Render<TicketEdit>();

        cut.Find("#title").Change("My Title");
        cut.Find("#description").Change("Some description");
        // Phase 2.8 step 24: reporter is now a <select> populated from team
        // membership. SeedReporterId was registered as a member of SeedTeamId
        // in the fixture so the option exists.
        cut.Find("#reporter").Change(SeedReporterId.ToString());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            _service.Verify(
                s => s.CreateAsync(It.IsAny<TicketDto>(), It.IsAny<CancellationToken>()),
                Times.Once);
            captured!.Title.Should().Be("My Title");
        });

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().EndWith("/tickets");
    }

    [Fact]
    public void Should_CallUpdateAndNavigate_When_ExistingTicketFormSubmitted()
    {
        var dto = new TicketDto
        {
            Id = 7,
            Title = "Old",
            Description = "Old",
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
        };
        _service
            .Setup(s => s.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);
        _service
            .Setup(s => s.UpdateAsync(It.IsAny<TicketDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = Render<TicketEdit>(p => p.Add(c => c.Id, 7));
        cut.WaitForAssertion(() => cut.Find("#title").GetAttribute("value").Should().Be("Old"));

        cut.Find("#title").Change("New");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            _service.Verify(
                s => s.UpdateAsync(It.IsAny<TicketDto>(), It.IsAny<CancellationToken>()),
                Times.Once);
        });

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().EndWith("/tickets");
    }
}

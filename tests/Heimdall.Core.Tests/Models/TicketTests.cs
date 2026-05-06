using FluentAssertions;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests.Models;

public class TicketTests
{
    private static readonly Guid SeedProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SeedTeamId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SeedReporterId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SeedAssigneeId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void Should_HaveDefaults_When_NewlyConstructed()
    {
        var ticket = new Ticket();

        ticket.Id.Should().Be(0);
        ticket.Title.Should().BeEmpty();
        ticket.Description.Should().BeEmpty();
        ticket.Status.Should().Be(TicketStatus.Open);
        ticket.Priority.Should().Be(TicketPriority.Medium);
        ticket.ProjectId.Should().Be(Guid.Empty);
        ticket.TeamId.Should().Be(Guid.Empty);
        ticket.ReporterId.Should().Be(Guid.Empty);
        ticket.AssigneeId.Should().BeNull();
        ticket.DateCreated.Should().Be(default);
        ticket.DateUpdated.Should().Be(default);
    }

    [Fact]
    public void Should_PersistValues_When_PropertiesAssigned()
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = 5,
            Title = "T",
            Description = "D",
            Status = TicketStatus.Resolved,
            Priority = TicketPriority.High,
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
            AssigneeId = SeedAssigneeId,
            DateCreated = now,
            DateUpdated = now,
        };

        ticket.Should().BeEquivalentTo(new
        {
            Id = 5,
            Title = "T",
            Description = "D",
            Status = TicketStatus.Resolved,
            Priority = TicketPriority.High,
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
            AssigneeId = (Guid?)SeedAssigneeId,
            DateCreated = now,
            DateUpdated = now,
        });
    }

    [Theory]
    [InlineData(TicketStatus.Open, 0)]
    [InlineData(TicketStatus.InProgress, 1)]
    [InlineData(TicketStatus.Resolved, 2)]
    [InlineData(TicketStatus.Closed, 3)]
    public void Should_PreserveTicketStatusOrdinal_When_CastToInt(TicketStatus status, int expected)
    {
        ((int)status).Should().Be(expected);
    }

    [Theory]
    [InlineData(TicketPriority.Low, 0)]
    [InlineData(TicketPriority.Medium, 1)]
    [InlineData(TicketPriority.High, 2)]
    [InlineData(TicketPriority.Critical, 3)]
    public void Should_PreserveTicketPriorityOrdinal_When_CastToInt(TicketPriority priority, int expected)
    {
        ((int)priority).Should().Be(expected);
    }
}

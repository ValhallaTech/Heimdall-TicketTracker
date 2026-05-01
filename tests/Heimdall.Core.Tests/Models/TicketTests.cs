using FluentAssertions;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests.Models;

public class TicketTests
{
    [Fact]
    public void Should_HaveDefaults_When_NewlyConstructed()
    {
        var ticket = new Ticket();

        ticket.Id.Should().Be(0);
        ticket.Title.Should().BeEmpty();
        ticket.Description.Should().BeEmpty();
        ticket.Status.Should().Be(TicketStatus.Open);
        ticket.Priority.Should().Be(TicketPriority.Medium);
        ticket.Reporter.Should().BeEmpty();
        ticket.Assignee.Should().BeNull();
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
            Reporter = "r",
            Assignee = "a",
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
            Reporter = "r",
            Assignee = "a",
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

using Microsoft.Extensions.Logging.Abstractions;
using AutoMapper;
using FluentAssertions;
using Heimdall.BLL.Mapping;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;

namespace Heimdall.BLL.Tests.Mapping;

public class TicketProfileTests
{
    private static IMapper CreateMapper()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<TicketProfile>(), loggerFactory: NullLoggerFactory.Instance);
        return config.CreateMapper();
    }

    [Fact]
    public void Should_HaveValidConfiguration_When_AssertedAtIntegrationLevel()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<TicketProfile>(), loggerFactory: NullLoggerFactory.Instance);
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Should_MapAllProperties_When_TicketToDto()
    {
        var mapper = CreateMapper();
        var now = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = 1,
            Title = "T",
            Description = "D",
            Status = TicketStatus.Resolved,
            Priority = TicketPriority.High,
            Reporter = "r",
            Assignee = "a",
            DateCreated = now,
            DateUpdated = now.AddMinutes(1),
        };

        var dto = mapper.Map<TicketDto>(ticket);

        dto.Should().BeEquivalentTo(ticket);
    }

    [Fact]
    public void Should_IgnoreDateCreatedAndDateUpdated_When_DtoToTicket()
    {
        var mapper = CreateMapper();
        var dto = new TicketDto
        {
            Id = 7,
            Title = "T",
            Description = "D",
            Status = TicketStatus.InProgress,
            Priority = TicketPriority.Critical,
            Reporter = "r",
            Assignee = "a",
            DateCreated = DateTimeOffset.UtcNow,
            DateUpdated = DateTimeOffset.UtcNow,
        };

        var ticket = mapper.Map<Ticket>(dto);

        ticket.Id.Should().Be(dto.Id);
        ticket.Title.Should().Be(dto.Title);
        ticket.Description.Should().Be(dto.Description);
        ticket.Status.Should().Be(dto.Status);
        ticket.Priority.Should().Be(dto.Priority);
        ticket.Reporter.Should().Be(dto.Reporter);
        ticket.Assignee.Should().Be(dto.Assignee);
        ticket.DateCreated.Should().Be(default);
        ticket.DateUpdated.Should().Be(default);
    }
}

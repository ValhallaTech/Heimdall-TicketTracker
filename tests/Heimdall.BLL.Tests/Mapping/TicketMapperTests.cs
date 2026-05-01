using FluentAssertions;
using Heimdall.BLL.Mapping;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;
using Mapster;

namespace Heimdall.BLL.Tests.Mapping;

/// <summary>
/// Tests for the source-generated <see cref="TicketMapper"/>. The implementation
/// (<c>src/Heimdall.BLL/Mappers/TicketMapper.cs</c>) is produced by Mapster.Tool from
/// <see cref="ITicketMapper"/> and <see cref="TicketMappingRegister"/>. These tests pin
/// the generated behavior — including the <c>DateCreated</c>/<c>DateUpdated</c> ignore
/// contract — so a stale regeneration is caught at CI time.
/// </summary>
public class TicketMapperTests
{
    private static ITicketMapper CreateMapper() => new TicketMapper();

    [Fact]
    public void Should_RegisterApplyIgnoreRules_When_TypeAdapterConfigCompiled()
    {
        // Sanity check that the IRegister source-of-truth is internally consistent —
        // Compile() throws if any NewConfig is unmappable.
        var config = new TypeAdapterConfig();
        new TicketMappingRegister().Register(config);

        Action act = () => config.Compile();

        act.Should().NotThrow();
    }

    [Fact]
    public void Should_Throw_When_RegisterCalledWithNullConfig()
    {
        var register = new TicketMappingRegister();

        Action act = () => register.Register(null!);

        act.Should().Throw<ArgumentNullException>();
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

        var dto = mapper.Map(ticket);

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

        var ticket = mapper.Map(dto);

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

    [Fact]
    public void Should_MapAllElements_When_TicketListToDtoList()
    {
        var mapper = CreateMapper();
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<Ticket> source = new[]
        {
            new Ticket
            {
                Id = 1,
                Title = "A",
                Description = "DA",
                Status = TicketStatus.Open,
                Priority = TicketPriority.Low,
                Reporter = "r1",
                Assignee = null,
                DateCreated = now,
                DateUpdated = now,
            },
            new Ticket
            {
                Id = 2,
                Title = "B",
                Description = "DB",
                Status = TicketStatus.Closed,
                Priority = TicketPriority.High,
                Reporter = "r2",
                Assignee = "a2",
                DateCreated = now.AddMinutes(-5),
                DateUpdated = now,
            },
        };

        var dtos = mapper.Map(source);

        dtos.Should().HaveCount(2);
        dtos.Should().BeEquivalentTo(source);
    }
}

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
/// contract — so a stale regeneration is caught at CI time. Phase 2.5 replaced the
/// legacy <c>Reporter</c>/<c>Assignee</c> string columns with FK Guid columns; the
/// assertions below reflect that schema.
/// </summary>
public class TicketMapperTests
{
    private static readonly Guid SeedProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SeedTeamId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SeedReporterId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SeedAssigneeId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static ITicketMapper CreateMapper() => new TicketMapper();

    [Fact]
    public void Should_RegisterApplyIgnoreRules_When_TypeAdapterConfigCompiled()
    {
        // Sanity check that the IRegister source-of-truth is internally consistent:
        // applying TicketMappingRegister to a fresh TypeAdapterConfig must produce
        // a configuration that compiles successfully (no missing/ambiguous mappings).
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
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
            AssigneeId = SeedAssigneeId,
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
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
            AssigneeId = SeedAssigneeId,
            DateCreated = DateTimeOffset.UtcNow,
            DateUpdated = DateTimeOffset.UtcNow,
        };

        var ticket = mapper.Map(dto);

        ticket.Id.Should().Be(dto.Id);
        ticket.Title.Should().Be(dto.Title);
        ticket.Description.Should().Be(dto.Description);
        ticket.Status.Should().Be(dto.Status);
        ticket.Priority.Should().Be(dto.Priority);
        ticket.ProjectId.Should().Be(dto.ProjectId);
        ticket.TeamId.Should().Be(dto.TeamId);
        ticket.ReporterId.Should().Be(dto.ReporterId);
        ticket.AssigneeId.Should().Be(dto.AssigneeId);
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
                ProjectId = SeedProjectId,
                TeamId = SeedTeamId,
                ReporterId = SeedReporterId,
                AssigneeId = null,
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
                ProjectId = SeedProjectId,
                TeamId = SeedTeamId,
                ReporterId = SeedReporterId,
                AssigneeId = SeedAssigneeId,
                DateCreated = now.AddMinutes(-5),
                DateUpdated = now,
            },
        };

        var dtos = mapper.Map(source);

        dtos.Should().HaveCount(2);
        dtos.Should().BeEquivalentTo(source);
    }
}

using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests.Dtos;

public class TicketDtoTests
{
    private static readonly Guid SeedProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SeedTeamId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid SeedReporterId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void Should_HaveDefaults_When_NewlyConstructed()
    {
        var dto = new TicketDto();

        dto.Id.Should().Be(0);
        dto.Title.Should().BeEmpty();
        dto.Description.Should().BeEmpty();
        dto.Status.Should().Be(TicketStatus.Open);
        dto.Priority.Should().Be(TicketPriority.Medium);
        dto.ProjectId.Should().Be(Guid.Empty);
        dto.TeamId.Should().Be(Guid.Empty);
        dto.ReporterId.Should().Be(Guid.Empty);
        dto.AssigneeId.Should().BeNull();
    }

    [Fact]
    public void Should_FailValidation_When_RequiredFieldsAreEmpty()
    {
        // Title / Description are still string-required; ProjectId / TeamId /
        // ReporterId are [Required] on the DTO post-Phase-2.5 but Guid is a
        // value type so [Required] on a non-nullable Guid does NOT flag
        // Guid.Empty (the framework treats default(Guid) as "present"). We
        // therefore only assert the string members.
        var dto = new TicketDto();
        var ctx = new ValidationContext(dto);
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(dto, ctx, results, validateAllProperties: true);

        ok.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Title)));
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Description)));
    }

    [Fact]
    public void Should_PassValidation_When_RequiredFieldsArePopulated()
    {
        var dto = new TicketDto
        {
            Title = "Title",
            Description = "Desc",
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
        };
        var ctx = new ValidationContext(dto);
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(dto, ctx, results, validateAllProperties: true);

        ok.Should().BeTrue();
        results.Should().BeEmpty();
    }

    [Fact]
    public void Should_FailValidation_When_TitleExceedsMaxLength()
    {
        var dto = new TicketDto
        {
            Title = new string('x', 201),
            Description = "Desc",
            ProjectId = SeedProjectId,
            TeamId = SeedTeamId,
            ReporterId = SeedReporterId,
        };
        var ctx = new ValidationContext(dto);
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(dto, ctx, results, validateAllProperties: true);

        ok.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Title)));
    }
}

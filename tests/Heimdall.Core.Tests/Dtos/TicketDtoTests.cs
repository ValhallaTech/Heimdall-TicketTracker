using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Heimdall.Core.Dtos;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests.Dtos;

public class TicketDtoTests
{
    [Fact]
    public void Should_HaveDefaults_When_NewlyConstructed()
    {
        var dto = new TicketDto();

        dto.Id.Should().Be(0);
        dto.Title.Should().BeEmpty();
        dto.Description.Should().BeEmpty();
        dto.Status.Should().Be(TicketStatus.Open);
        dto.Priority.Should().Be(TicketPriority.Medium);
        dto.Reporter.Should().BeEmpty();
        dto.Assignee.Should().BeNull();
    }

    [Fact]
    public void Should_FailValidation_When_RequiredFieldsAreEmpty()
    {
        var dto = new TicketDto();
        var ctx = new ValidationContext(dto);
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(dto, ctx, results, validateAllProperties: true);

        ok.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Title)));
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Description)));
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Reporter)));
    }

    [Fact]
    public void Should_PassValidation_When_RequiredFieldsArePopulated()
    {
        var dto = new TicketDto
        {
            Title = "Title",
            Description = "Desc",
            Reporter = "Me",
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
            Reporter = "Me",
        };
        var ctx = new ValidationContext(dto);
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(dto, ctx, results, validateAllProperties: true);

        ok.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(TicketDto.Title)));
    }
}

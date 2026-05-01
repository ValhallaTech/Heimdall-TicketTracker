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
    private readonly Mock<ITicketService> _service = new(MockBehavior.Loose);

    public TicketEditTests()
    {
        Services.AddSingleton(_service.Object);
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
        var dto = new TicketDto
        {
            Id = 42,
            Title = "Existing Title",
            Description = "Existing Desc",
            Status = TicketStatus.InProgress,
            Priority = TicketPriority.High,
            Reporter = "alice",
            Assignee = "bob",
        };
        _service
            .Setup(s => s.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var cut = Render<TicketEdit>(p => p.Add(c => c.Id, 42));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Edit Ticket");
            cut.Find("#title").GetAttribute("value").Should().Be("Existing Title");
            cut.Find("#reporter").GetAttribute("value").Should().Be("alice");
            cut.Find("#assignee").GetAttribute("value").Should().Be("bob");
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
        cut.Find("#reporter").Change("alice");
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
            Reporter = "alice",
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

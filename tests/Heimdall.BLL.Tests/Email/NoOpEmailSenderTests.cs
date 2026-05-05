using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Core.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.BLL.Tests.Email;

public class NoOpEmailSenderTests
{
    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new NoOpEmailSender(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_MessageIsNull()
    {
        var sut = new NoOpEmailSender(NullLogger<NoOpEmailSender>.Instance);
        Func<Task> act = () => sut.SendAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_LogAndComplete_When_SendAsyncCalled()
    {
        var logger = new Mock<ILogger<NoOpEmailSender>>();
        var sut = new NoOpEmailSender(logger.Object);
        var message = new EmailMessage
        {
            To = "user@example.test",
            Subject = "Hello",
            HtmlBody = "<p>hi</p>",
        };

        await sut.SendAsync(message);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

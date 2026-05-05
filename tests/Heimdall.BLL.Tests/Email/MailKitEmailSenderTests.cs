using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Core.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Heimdall.BLL.Tests.Email;

public class MailKitEmailSenderTests
{
    private static IOptions<SmtpOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new SmtpOptions
        {
            Host = "smtp.test.local",
            Port = 587,
            UserName = "user",
            Password = "pass",
            From = "sender@test.local",
            UseStartTls = true,
        });

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new MailKitEmailSender(null!, NullLogger<MailKitEmailSender>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new MailKitEmailSender(Options(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_MessageIsNull()
    {
        var sut = new MailKitEmailSender(Options(), NullLogger<MailKitEmailSender>.Instance);
        Func<Task> act = () => sut.SendAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("", "Subject", "<p>body</p>", null)]
    [InlineData("   ", "Subject", "<p>body</p>", null)]
    [InlineData("user@example.test", "", "<p>body</p>", null)]
    [InlineData("user@example.test", "   ", "<p>body</p>", null)]
    [InlineData("user@example.test", "Subject", null, null)]
    public async Task Should_ThrowArgumentException_When_MessageInvalid(
        string to,
        string subject,
        string? html,
        string? text)
    {
        var sut = new MailKitEmailSender(Options(), NullLogger<MailKitEmailSender>.Instance);
        var message = new EmailMessage
        {
            To = to,
            Subject = subject,
            HtmlBody = html,
            PlainTextBody = text,
        };

        Func<Task> act = () => sut.SendAsync(message);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}

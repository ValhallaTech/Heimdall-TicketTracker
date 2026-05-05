using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Web.Email;

namespace Heimdall.Web.Tests.Email;

/// <summary>
/// Unit tests for <see cref="EmailFlowGate"/>. Drives the gate against a
/// stub <see cref="EmailSenderRegistrationInfo"/> instead of standing up
/// the full DI graph, so each branch (active / inactive / null / reason) is
/// asserted in isolation.
/// </summary>
public class EmailFlowGateTests
{
    [Fact]
    public void Should_BeActive_When_MailKitChosen()
    {
        var info = new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "MailKitEmailSender",
            Reason = "Email:Smtp Host/UserName/Password/From all configured",
        };

        var gate = new EmailFlowGate(info);

        gate.IsActive.Should().BeTrue();
        gate.Reason.Should().BeEmpty();
    }

    [Fact]
    public void Should_NotBeActive_When_NoOpChosen()
    {
        var info = new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "NoOpEmailSender",
            Reason = "Missing one or more of Email:Smtp Host/UserName/Password/From: Host",
        };

        var gate = new EmailFlowGate(info);

        gate.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Should_Throw_When_InfoIsNull()
    {
        Action act = () => _ = new EmailFlowGate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_ExposeReason_When_NotActive()
    {
        var info = new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "NoOpEmailSender",
            Reason = "stub",
        };

        var gate = new EmailFlowGate(info);

        gate.IsActive.Should().BeFalse();
        gate.Reason.Should().Be(
            "Email sender is the no-op fallback; configure SMTP to enable email-driven flows.");
    }
}

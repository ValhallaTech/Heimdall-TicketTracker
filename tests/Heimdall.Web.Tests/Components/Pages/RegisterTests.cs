using Bunit;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Web.Components.Pages;
using Heimdall.Web.Email;
using Heimdall.Web.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Tests.Components.Pages;

/// <summary>
/// bUnit tests for <see cref="Register"/>. The page is gated on BOTH
/// <see cref="EmailFlowGate.IsActive"/> and
/// <see cref="RegistrationOptions.Enabled"/>; either being off renders a
/// "currently disabled" card.
/// </summary>
public class RegisterTests : BunitContext
{
    private void RegisterServices(string implementation, bool registrationEnabled)
    {
        Services.AddSingleton(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = implementation,
            Reason = "test",
        });
        Services.AddSingleton<EmailFlowGate>();
        Services.AddSingleton<IOptions<RegistrationOptions>>(
            Options.Create(new RegistrationOptions { Enabled = registrationEnabled }));
    }

    [Fact]
    public void Should_RenderForm_When_GateActiveAndRegistrationEnabled()
    {
        RegisterServices("MailKitEmailSender", registrationEnabled: true);

        var cut = Render<Register>();

        cut.Markup.Should().Contain("action=\"/account/register\"");
        cut.Markup.Should().Contain("name=\"email\"");
        cut.Markup.Should().Contain("name=\"password\"");
        cut.Markup.Should().Contain("name=\"confirmPassword\"");
        cut.Markup.Should().Contain("autocomplete=\"new-password\"");
        cut.Markup.Should().NotContain("currently disabled");
    }

    [Fact]
    public void Should_RenderDisabledBanner_When_GateInactive()
    {
        RegisterServices("NoOpEmailSender", registrationEnabled: true);

        var cut = Render<Register>();

        cut.Markup.Should().Contain("currently disabled");
        cut.Markup.Should().NotContain("action=\"/account/register\"");
    }

    [Fact]
    public void Should_RenderDisabledBanner_When_RegistrationDisabled()
    {
        RegisterServices("MailKitEmailSender", registrationEnabled: false);

        var cut = Render<Register>();

        cut.Markup.Should().Contain("currently disabled");
        cut.Markup.Should().NotContain("action=\"/account/register\"");
    }
}

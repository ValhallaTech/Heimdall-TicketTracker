using Bunit;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Web.Components.Pages;
using Heimdall.Web.Email;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Web.Tests.Components.Pages;

/// <summary>
/// bUnit tests for <see cref="ForgotPassword"/>. The page injects
/// <see cref="EmailFlowGate"/> and toggles between an active form and a
/// "currently unavailable" banner based on its state.
/// </summary>
public class ForgotPasswordTests : BunitContext
{
    private static EmailSenderRegistrationInfo Active() => new()
    {
        ChosenImplementation = "MailKitEmailSender",
        Reason = "test",
    };

    private static EmailSenderRegistrationInfo Inactive() => new()
    {
        ChosenImplementation = "NoOpEmailSender",
        Reason = "test",
    };

    [Fact]
    public void Should_RenderForm_When_GateActive()
    {
        Services.AddSingleton(Active());
        Services.AddSingleton<EmailFlowGate>();

        var cut = Render<ForgotPassword>();

        cut.Markup.Should().Contain("action=\"/account/forgot-password\"");
        cut.Markup.Should().Contain("name=\"email\"");
        cut.Markup.Should().Contain("autocomplete=\"email\"");
        cut.Markup.Should().NotContain("currently unavailable");
    }

    [Fact]
    public void Should_RenderDisabledBanner_When_GateInactive()
    {
        Services.AddSingleton(Inactive());
        Services.AddSingleton<EmailFlowGate>();

        var cut = Render<ForgotPassword>();

        cut.Markup.Should().Contain("currently unavailable");
        cut.Markup.Should().NotContain("action=\"/account/forgot-password\"");
    }

    [Fact]
    public void Should_RenderDisabledBanner_When_ErrorParameterIsDisabled()
    {
        Services.AddSingleton(Active());
        Services.AddSingleton<EmailFlowGate>();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("Error", "disabled"));

        var cut = Render<ForgotPassword>();

        cut.Markup.Should().Contain("currently unavailable");
    }
}

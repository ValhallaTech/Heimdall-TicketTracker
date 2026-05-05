using Bunit;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Web.Components.Pages;
using Heimdall.Web.Email;
using Heimdall.Web.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.Web.Tests.Components.Pages;

/// <summary>
/// bUnit tests for <see cref="Login"/>. The page is server-rendered (no
/// <c>@rendermode InteractiveServer</c>) and posts to <c>/account/login</c>.
/// <c>&lt;AntiforgeryToken /&gt;</c> requires a cascaded <c>HttpContext</c>;
/// in bUnit there is none, so the component renders nothing for the token —
/// these tests assert the surrounding form structure (the real token is
/// emitted at runtime by the framework). The <c>[SupplyParameterFromQuery]</c>
/// parameters are populated via <c>NavigationManager.NavigateTo</c> with the
/// appropriate query string, per bUnit's required pattern.
/// </summary>
public class LoginPageTests : BunitContext
{
    public LoginPageTests()
    {
        // Login.razor injects EmailFlowGate + IOptions<RegistrationOptions> to
        // decide whether to advertise the "Register" link. Default both to off.
        Services.AddSingleton(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "NoOpEmailSender",
            Reason = "test default",
        });
        Services.AddSingleton<EmailFlowGate>();
        Services.AddSingleton<IOptions<RegistrationOptions>>(
            Options.Create(new RegistrationOptions { Enabled = false }));
    }

    [Fact]
    public void Should_RenderForm_When_RenderedAnonymously()
    {
        var cut = Render<Login>();

        cut.Markup.Should().Contain("action=\"/account/login\"");
        cut.Markup.Should().Contain("method=\"post\"");
        cut.Markup.Should().Contain("name=\"email\"");
        cut.Markup.Should().Contain("name=\"password\"");
        cut.Markup.Should().Contain("autocomplete=\"current-password\"");
    }

    [Fact]
    public void Should_RenderError_When_ErrorParameterIsInvalidCredentials()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("Error", "invalid-credentials"));

        var cut = Render<Login>();

        cut.Markup.Should().Contain("Invalid email or password");
        cut.Markup.Should().Contain("aria-invalid=\"true\"");
    }

    [Fact]
    public void Should_NotRenderError_When_ErrorParameterIsNull()
    {
        var cut = Render<Login>();

        cut.Markup.Should().NotContain("Invalid email or password");
        cut.Markup.Should().NotContain("aria-invalid=\"true\"");
    }

    [Fact]
    public void Should_RenderReturnUrl_When_ReturnUrlSupplied()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("ReturnUrl", "/tickets"));

        var cut = Render<Login>();

        cut.Markup.Should().Contain("name=\"returnUrl\"");
        cut.Markup.Should().Contain("value=\"/tickets\"");
    }

    [Fact]
    public void Should_OmitReturnUrl_When_NotSupplied()
    {
        var cut = Render<Login>();

        cut.Markup.Should().NotContain("name=\"returnUrl\"");
    }

    [Fact]
    public void Should_RenderForgotPasswordLink_Always()
    {
        var cut = Render<Login>();

        cut.Markup.Should().Contain("/forgot-password");
        cut.Markup.Should().Contain("Forgot your password?");
    }

    [Fact]
    public void Should_NotRenderRegisterLink_When_GateInactiveOrRegistrationDisabled()
    {
        // Both gate (NoOp) and Registration:Enabled=false in the constructor.
        var cut = Render<Login>();

        cut.Markup.Should().NotContain("/register");
        cut.Markup.Should().NotContain("Don't have an account");
    }

    [Fact]
    public void Should_RenderRegisterLink_When_GateActiveAndRegistrationEnabled()
    {
        // Replace the registrations with active values.
        Services.AddSingleton(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "MailKitEmailSender",
            Reason = "test active",
        });

        // Use a bUnit context that re-resolves EmailFlowGate against the new info.
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "MailKitEmailSender",
            Reason = "test active",
        });
        ctx.Services.AddSingleton<EmailFlowGate>();
        ctx.Services.AddSingleton<IOptions<RegistrationOptions>>(
            Options.Create(new RegistrationOptions { Enabled = true }));

        var cut = ctx.Render<Login>();

        cut.Markup.Should().Contain("/register");
        cut.Markup.Should().Contain("Don't have an account");
    }
}



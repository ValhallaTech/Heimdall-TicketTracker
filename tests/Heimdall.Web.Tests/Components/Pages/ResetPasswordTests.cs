using Bunit;
using FluentAssertions;
using Heimdall.BLL.Email;
using Heimdall.Web.Components.Pages;
using Heimdall.Web.Email;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Web.Tests.Components.Pages;

/// <summary>
/// bUnit tests for <see cref="ResetPassword"/>. Asserts the form renders the
/// hidden email + token from the query string, surfaces error variants, and
/// suppresses the form entirely when <see cref="EmailFlowGate.IsActive"/> is
/// false.
/// </summary>
public class ResetPasswordTests : BunitContext
{
    public ResetPasswordTests()
    {
        Services.AddSingleton(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "MailKitEmailSender",
            Reason = "test",
        });
        Services.AddSingleton<EmailFlowGate>();
    }

    [Fact]
    public void Should_RenderFormWithEmailAndToken_When_QueryParamsSupplied()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var uri = nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["Email"] = "user@example.com",
            ["Token"] = "raw-token",
        });
        nav.NavigateTo(uri);

        var cut = Render<ResetPassword>();

        cut.Markup.Should().Contain("action=\"/account/reset-password\"");
        cut.Markup.Should().Contain("name=\"email\"");
        cut.Markup.Should().Contain("value=\"user@example.com\"");
        cut.Markup.Should().Contain("name=\"token\"");
        cut.Markup.Should().Contain("value=\"raw-token\"");
        cut.Markup.Should().Contain("autocomplete=\"new-password\"");
    }

    [Fact]
    public void Should_RenderInvalidTokenAlert_When_ErrorIsInvalidToken()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("Error", "invalid-token"));

        var cut = Render<ResetPassword>();

        cut.Markup.Should().Contain("invalid or has expired");
        cut.Markup.Should().Contain("aria-invalid=\"true\"");
    }

    [Fact]
    public void Should_RenderMismatchAlert_When_ErrorIsPasswordsMismatch()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("Error", "passwords-mismatch"));

        var cut = Render<ResetPassword>();

        cut.Markup.Should().Contain("passwords did not match");
    }
}

/// <summary>
/// bUnit tests for <see cref="ResetPassword"/>'s defense-in-depth
/// "gate inactive" branch.
/// </summary>
public class ResetPasswordDisabledTests : BunitContext
{
    [Fact]
    public void Should_RenderUnavailableBanner_When_GateInactive()
    {
        Services.AddSingleton(new EmailSenderRegistrationInfo
        {
            ChosenImplementation = "NoOpEmailSender",
            Reason = "test",
        });
        Services.AddSingleton<EmailFlowGate>();

        var cut = Render<ResetPassword>();

        cut.Markup.Should().Contain("currently unavailable");
        cut.Markup.Should().NotContain("action=\"/account/reset-password\"");
    }
}

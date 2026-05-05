using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// Phase 1 step 11 acceptance suite — drives the assembled HTTP pipeline of
/// <c>Heimdall.Web</c> against a real Testcontainers Postgres to confirm the
/// authenticated foundation works end-to-end:
/// global authenticated-only gate, server-rendered cookie login, audit-event
/// persistence, and anonymous reachability of public pages.
/// </summary>
[Collection("Phase1Acceptance")]
public sealed class Phase1AcceptanceTests : IClassFixture<HeimdallWebApplicationFactory>
{
    private const string SeededEmail = "acceptance.login@example.com";
    private const string SeededPassword = "Acceptance!Login99";

    private readonly HeimdallWebApplicationFactory _factory;

    public Phase1AcceptanceTests(HeimdallWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Should_RedirectToLogin_When_AnonymousAccessesProtectedPage()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/tickets");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Contain("/login");
        response.Headers.Location.OriginalString.Should().Contain("returnUrl=");
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/")]
    [InlineData("/access-denied")]
    [InlineData("/forgot-password")]
    [InlineData("/forgot-password-confirmation")]
    [InlineData("/register-confirmation")]
    public async Task Should_BeAnonymouslyReachable_When_PublicPageRequested(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"public page {path} must be reachable without authentication");
    }

    [Fact]
    public async Task Should_SignInAndPersistAuditEvent_When_ValidCredentialsPosted()
    {
        await SeedUserAsync(_factory.Services, SeededEmail, SeededPassword);

        // WAF's default client uses an internal HttpClientHandler with a
        // CookieContainer when HandleCookies is true (the default). Cookies
        // round-trip automatically across the requests below.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // GET /login to prime the antiforgery cookie + scrape the form token.
        var loginPageResponse = await client.GetAsync("/login");
        loginPageResponse.EnsureSuccessStatusCode();
        var html = await loginPageResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(html);

        // POST credentials.
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("email", SeededEmail),
            new KeyValuePair<string, string>("password", SeededPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });
        var loginResponse = await client.PostAsync("/account/login", form);

        loginResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        loginResponse.Headers.Location!.OriginalString.Should().Be("/");

        // Auth cookie issued — visible in Set-Cookie on the login response.
        var setCookieValues = loginResponse.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : Array.Empty<string>();
        setCookieValues.Should()
            .Contain(c => c.StartsWith(".Heimdall.Auth=", StringComparison.Ordinal),
                because: "successful login must mint the auth cookie");

        // Re-request a protected page; the cookie is auto-attached by the handler.
        var protectedResponse = await client.GetAsync("/tickets");
        protectedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an authenticated request must reach the protected page");

        // Audit event persisted.
        var loginSuccessRows = await CountAuditEventsAsync("login.success");
        loginSuccessRows.Should().BeGreaterThan(0, because: "login.success must be audited");
    }

    private static async Task<HeimdallUser> SeedUserAsync(IServiceProvider rootServices, string email, string password)
    {
        await using var scope = rootServices.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HeimdallUser>>();

        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var user = new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = userManager.NormalizeEmail(email),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Acceptance fixture failed to seed user: "
                + string.Join(", ", result.Errors.Select(e => $"{e.Code}:{e.Description}")));
        }

        return user;
    }

    private async Task<int> CountAuditEventsAsync(string eventType)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM audit_events WHERE event_type = @EventType",
            new { EventType = eventType });
        return (int)count;
    }

    /// <summary>
    /// Pulls the <c>__RequestVerificationToken</c> hidden input value out of a
    /// rendered HTML form. The Blazor <c>&lt;AntiforgeryToken /&gt;</c> component
    /// emits the input with attributes in either order
    /// (<c>name=...</c> before/after <c>value=...</c>); both orderings are matched.
    /// </summary>
    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"");
        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "<input[^>]*value=\"(?<token>[^\"]+)\"[^>]*name=\"__RequestVerificationToken\"");
        }

        if (!match.Success)
        {
            throw new InvalidOperationException(
                "Antiforgery token hidden input not found in rendered HTML.");
        }

        return match.Groups["token"].Value;
    }
}

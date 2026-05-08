using System.Security.Claims;
using FluentAssertions;
using Heimdall.Core.Interfaces;
using Heimdall.Web.Authorization.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Heimdall.Web.Tests.Authorization.Policies;

/// <summary>
/// Unit tests for <see cref="SystemAdminAuthorizationHandler"/>. Deny-closed on
/// every failure path; only succeeds on a parseable id with system_admin = true.
/// </summary>
public class SystemAdminAuthorizationHandlerTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IUserLookup> _userLookup = new(MockBehavior.Strict);

    private SystemAdminAuthorizationHandler CreateSut() =>
        new(_userLookup.Object, NullLogger<SystemAdminAuthorizationHandler>.Instance);

    private static AuthorizationHandlerContext CreateContext(
        SystemAdminRequirement requirement,
        ClaimsPrincipal? user = null)
    {
        return new AuthorizationHandlerContext(
            new[] { requirement },
            user ?? new ClaimsPrincipal(new ClaimsIdentity()),
            resource: null);
    }

    private static ClaimsPrincipal Principal(string? nameIdentifier)
    {
        var claims = nameIdentifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, nameIdentifier) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Constructor_Should_Throw_When_UserLookupIsNull()
    {
        Action act = () => new SystemAdminAuthorizationHandler(
            null!, NullLogger<SystemAdminAuthorizationHandler>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Should_Throw_When_LoggerIsNull()
    {
        Action act = () => new SystemAdminAuthorizationHandler(_userLookup.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Succeed_When_UserLookupReturnsTrue()
    {
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var requirement = new SystemAdminRequirement();
        var context = CreateContext(requirement, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Should_NotSucceed_When_UserLookupReturnsFalse()
    {
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var requirement = new SystemAdminRequirement();
        var context = CreateContext(requirement, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_NotSucceed_When_NameIdentifierClaimIsMissing()
    {
        var requirement = new SystemAdminRequirement();
        var context = CreateContext(requirement, Principal(null));

        await CreateSut().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        _userLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_NotSucceed_When_NameIdentifierIsUnparseable()
    {
        var requirement = new SystemAdminRequirement();
        var context = CreateContext(requirement, Principal("not-a-guid"));

        await CreateSut().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        _userLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_DenyClosed_When_LookupThrows()
    {
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var requirement = new SystemAdminRequirement();
        var context = CreateContext(requirement, Principal(Actor.ToString()));

        await CreateSut().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Should_PropagateOperationCanceled_When_LookupCancels()
    {
        _userLookup
            .Setup(u => u.IsSystemAdminAsync(Actor, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var requirement = new SystemAdminRequirement();
        var context = CreateContext(requirement, Principal(Actor.ToString()));

        Func<Task> act = () => CreateSut().HandleAsync(context);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

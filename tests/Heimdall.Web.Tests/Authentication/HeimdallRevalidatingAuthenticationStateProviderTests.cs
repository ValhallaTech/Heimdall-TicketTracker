using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.Web.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Heimdall.Web.Tests.Authentication;

public class HeimdallRevalidatingAuthenticationStateProviderTests
{
    private const string SecurityStampClaimType = "AspNet.Identity.SecurityStamp";

    [Fact]
    public async Task Should_ReturnTrue_When_PrincipalIsAnonymous()
    {
        // Arrange
        var harness = new TestHarness();
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        harness.UserStoreMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_NameIdentifierClaimMissing()
    {
        // Arrange
        var harness = new TestHarness();
        var identity = new ClaimsIdentity(authenticationType: "Cookies");
        identity.AddClaim(new Claim(SecurityStampClaimType, "stamp"));
        var state = new AuthenticationState(new ClaimsPrincipal(identity));

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        harness.UserStoreMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_NameIdentifierClaimIsNotAGuid()
    {
        // Arrange
        var harness = new TestHarness();
        var state = BuildState("not-a-guid", "stamp");

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        harness.UserStoreMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_UserNoLongerExists()
    {
        // Arrange
        var harness = new TestHarness();
        var userId = Guid.NewGuid();
        harness.UserStoreMock
            .Setup(s => s.FindByIdAsync(userId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HeimdallUser?)null);
        var state = BuildState(userId.ToString(), "stamp");

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_SecurityStampClaimMissing()
    {
        // Arrange
        var harness = new TestHarness();
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity(authenticationType: "Cookies");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        var state = new AuthenticationState(new ClaimsPrincipal(identity));

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        harness.UserStoreMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_StoredAndPrincipalSecurityStampsDiffer()
    {
        // Arrange
        var harness = new TestHarness();
        var userId = Guid.NewGuid();
        harness.UserStoreMock
            .Setup(s => s.FindByIdAsync(userId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeimdallUser { Id = userId, SecurityStamp = "stored-stamp" });
        var state = BuildState(userId.ToString(), "principal-stamp");

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Should_ReturnTrue_When_StoredAndPrincipalSecurityStampsMatch()
    {
        // Arrange
        var harness = new TestHarness();
        var userId = Guid.NewGuid();
        const string stamp = "matching-stamp";
        harness.UserStoreMock
            .Setup(s => s.FindByIdAsync(userId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeimdallUser { Id = userId, SecurityStamp = stamp });
        var state = BuildState(userId.ToString(), stamp);

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_FindByIdAsyncThrowsUnexpectedException()
    {
        // Arrange
        var harness = new TestHarness();
        var userId = Guid.NewGuid();
        harness.UserStoreMock
            .Setup(s => s.FindByIdAsync(userId.ToString(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB blew up"));
        var state = BuildState(userId.ToString(), "stamp");

        // Act
        var result = await harness.Provider.CallValidateAsync(state, CancellationToken.None);

        // Assert (fail-secure)
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Should_PropagateOperationCanceledException_When_CancellationRequested()
    {
        // Arrange
        var harness = new TestHarness();
        var userId = Guid.NewGuid();
        harness.UserStoreMock
            .Setup(s => s.FindByIdAsync(userId.ToString(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var state = BuildState(userId.ToString(), "stamp");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = () => harness.Provider.CallValidateAsync(state, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Should_Throw_When_AuthenticationStateIsNull()
    {
        // Arrange
        var harness = new TestHarness();

        // Act
        Func<Task> act = () => harness.Provider.CallValidateAsync(null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Should_Throw_When_LoggerFactoryIsNull()
    {
        // Arrange
        var scopeFactory = Mock.Of<IServiceScopeFactory>();
        var options = Options.Create(new IdentityOptions());

        // Act
        Action act = () => _ = new HeimdallRevalidatingAuthenticationStateProvider(
            null!,
            scopeFactory,
            options);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("loggerFactory");
    }

    [Fact]
    public void Should_Throw_When_ScopeFactoryIsNull()
    {
        // Arrange
        var options = Options.Create(new IdentityOptions());

        // Act
        Action act = () => _ = new HeimdallRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            null!,
            options);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("scopeFactory");
    }

    [Fact]
    public void Should_Throw_When_IdentityOptionsIsNull()
    {
        // Arrange
        var scopeFactory = Mock.Of<IServiceScopeFactory>();

        // Act
        Action act = () => _ = new HeimdallRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            scopeFactory,
            null!);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("identityOptions");
    }

    [Fact]
    public void Should_Return5Minutes_When_RevalidationIntervalQueried()
    {
        // Arrange
        var harness = new TestHarness();

        // Act / Assert
        harness.Provider.RevalidationIntervalForTest.Should().Be(TimeSpan.FromMinutes(5));
    }

    private static AuthenticationState BuildState(string userId, string securityStamp)
    {
        var identity = new ClaimsIdentity(authenticationType: "Cookies");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));
        identity.AddClaim(new Claim(SecurityStampClaimType, securityStamp));
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    private sealed class TestHarness
    {
        private static readonly System.Reflection.MethodInfo ValidateMethod =
            typeof(HeimdallRevalidatingAuthenticationStateProvider).GetMethod(
                "ValidateAuthenticationStateAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        private static readonly System.Reflection.PropertyInfo IntervalProperty =
            typeof(HeimdallRevalidatingAuthenticationStateProvider).GetProperty(
                "RevalidationInterval",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        public TestHarness()
        {
            UserStoreMock = new Mock<IUserStore<HeimdallUser>>(MockBehavior.Strict);

            var services = new ServiceCollection();
            services.AddSingleton(UserStoreMock.Object);
            ServiceProvider = services.BuildServiceProvider();

            var scopeFactory = ServiceProvider.GetRequiredService<IServiceScopeFactory>();
            var options = Options.Create(new IdentityOptions());
            // Default Identity claim type — kept explicit so the test does not depend
            // on the framework default at runtime.
            options.Value.ClaimsIdentity.SecurityStampClaimType = SecurityStampClaimType;

            var inner = new HeimdallRevalidatingAuthenticationStateProvider(
                NullLoggerFactory.Instance,
                scopeFactory,
                options);
            Provider = new ProviderProxy(inner, ValidateMethod, IntervalProperty);
        }

        public Mock<IUserStore<HeimdallUser>> UserStoreMock { get; }

        public ServiceProvider ServiceProvider { get; }

        public ProviderProxy Provider { get; }
    }

    /// <summary>
    /// Thin reflection-based proxy that exposes the protected
    /// <c>ValidateAuthenticationStateAsync</c> method and the
    /// <c>RevalidationInterval</c> property of the sealed provider so the unit tests
    /// can drive them directly. The provider is sealed by design (Phase 1 step 5
    /// spec); reflection is the documented escape hatch.
    /// </summary>
    private sealed class ProviderProxy
    {
        private readonly HeimdallRevalidatingAuthenticationStateProvider _inner;
        private readonly System.Reflection.MethodInfo _validateMethod;
        private readonly System.Reflection.PropertyInfo _intervalProperty;

        public ProviderProxy(
            HeimdallRevalidatingAuthenticationStateProvider inner,
            System.Reflection.MethodInfo validateMethod,
            System.Reflection.PropertyInfo intervalProperty)
        {
            _inner = inner;
            _validateMethod = validateMethod;
            _intervalProperty = intervalProperty;
        }

        public TimeSpan RevalidationIntervalForTest =>
            (TimeSpan)_intervalProperty.GetValue(_inner)!;

        public async Task<bool> CallValidateAsync(AuthenticationState state, CancellationToken ct)
        {
            try
            {
                var task = (Task<bool>)_validateMethod.Invoke(_inner, new object?[] { state, ct })!;
                return await task;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // Unwrap so callers see the real exception (e.g., ArgumentNullException).
                throw ex.InnerException;
            }
        }
    }
}

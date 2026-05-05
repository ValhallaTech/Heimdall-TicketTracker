using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Extensions;
using Heimdall.DAL.Identity;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.DAL.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="HeimdallUserStore"/>. Each test runs against a
/// real Postgres container provided by <see cref="PostgresFixture"/>, with the
/// <c>users</c> table reset before each test for determinism.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class HeimdallUserStoreTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly HeimdallUserStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeimdallUserStoreTests"/> class.
    /// </summary>
    /// <param name="fx">The shared Postgres fixture.</param>
    public HeimdallUserStoreTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _store = new HeimdallUserStore(options);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fx.ResetUsersTableAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Builds a fresh <see cref="HeimdallUser"/> with the supplied email (and the same
    /// upper-cased value as <c>NormalizedEmail</c>) plus reasonable defaults for the
    /// remaining required columns.
    /// </summary>
    /// <param name="email">The plain (case-preserved) email address.</param>
    /// <returns>A fully-populated <see cref="HeimdallUser"/> ready to insert.</returns>
    private static HeimdallUser Sample(string email = "user@example.com")
    {
        return new HeimdallUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            EmailConfirmed = false,
            LockoutEnabled = true,
        };
    }

    // -----------------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------------

    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new HeimdallUserStore(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------
    // CreateAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_AssignIdAndPersist_When_CreateAsyncCalled()
    {
        var user = Sample("create@example.com");

        var result = await _store.CreateAsync(user, CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.Email.Should().Be("create@example.com");
        fetched.NormalizedEmail.Should().Be("CREATE@EXAMPLE.COM");
        fetched.CreatedAt.Should().BeAfter(DateTimeOffset.UnixEpoch);
        fetched.UpdatedAt.Should().BeAfter(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Should_ReturnDuplicateEmail_When_NormalizedEmailAlreadyExists()
    {
        var first = Sample("dup@example.com");
        (await _store.CreateAsync(first, CancellationToken.None)).Succeeded.Should().BeTrue();

        var second = Sample("dup@example.com");
        var result = await _store.CreateAsync(second, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "DuplicateEmail");
    }

    [Fact]
    public async Task Should_Throw_When_CreateAsyncUserIsNull()
    {
        Func<Task> act = () => _store.CreateAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------
    // FindByIdAsync / FindByNameAsync / FindByEmailAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RoundTripUser_When_FindByIdAsyncCalledWithValidGuid()
    {
        var user = Sample("find@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(user.Id);
        fetched.Email.Should().Be(user.Email);
        fetched.NormalizedEmail.Should().Be(user.NormalizedEmail);
        fetched.SecurityStamp.Should().Be(user.SecurityStamp);
        fetched.ConcurrencyStamp.Should().Be(user.ConcurrencyStamp);
    }

    [Fact]
    public async Task Should_ReturnNull_When_FindByIdAsyncGivenNonExistentId()
    {
        var result = await _store.FindByIdAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnNull_When_FindByIdAsyncGivenMalformedGuidString()
    {
        var result = await _store.FindByIdAsync("not-a-guid", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Should_FindUser_When_FindByNameAsyncMatchesNormalizedEmail()
    {
        var user = Sample("byname@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        var fetched = await _store.FindByNameAsync(user.NormalizedEmail, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task Should_FindUser_When_FindByEmailAsyncMatchesNormalizedEmail()
    {
        var user = Sample("byemail@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        var fetched = await _store.FindByEmailAsync(user.NormalizedEmail, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task Should_ReturnNull_When_FindByEmailAsyncGivenUnknownEmail()
    {
        var fetched = await _store.FindByEmailAsync("MISSING@EXAMPLE.COM", CancellationToken.None);
        fetched.Should().BeNull();
    }

    // -----------------------------------------------------------------------------
    // UpdateAsync (concurrency)
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_UpdateAndRotateConcurrencyStamp_When_UpdateAsyncSucceeds()
    {
        var user = Sample("update@example.com");
        await _store.CreateAsync(user, CancellationToken.None);
        var originalStamp = user.ConcurrencyStamp;

        user.EmailConfirmed = true;
        var result = await _store.UpdateAsync(user, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        user.ConcurrencyStamp.Should().NotBe(originalStamp);

        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.EmailConfirmed.Should().BeTrue();
        fetched.ConcurrencyStamp.Should().Be(user.ConcurrencyStamp);
    }

    [Fact]
    public async Task Should_BumpUpdatedAt_When_UpdateAsyncSucceeds()
    {
        var user = Sample("bump@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        var preFetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        preFetched.Should().NotBeNull();
        var preUpdatedAt = preFetched!.UpdatedAt;

        // Sleep for slightly over a second to ensure the database clock advances past
        // any sub-second resolution and produces a strictly later timestamp.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        user.ConcurrencyStamp = preFetched.ConcurrencyStamp;
        user.EmailConfirmed = true;
        (await _store.UpdateAsync(user, CancellationToken.None)).Succeeded.Should().BeTrue();

        var postFetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        postFetched.Should().NotBeNull();
        postFetched!.UpdatedAt.Should().BeAfter(preUpdatedAt);
    }

    [Fact]
    public async Task Should_ReturnConcurrencyFailure_When_StampDoesNotMatch()
    {
        var user = Sample("stale@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        // Simulate a stale view: another process already updated the row, so the stamp
        // we hold no longer matches what's in the database.
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        var result = await _store.UpdateAsync(user, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "ConcurrencyFailure");
    }

    [Fact]
    public async Task Should_ReturnDuplicateEmail_When_UpdateAsyncCollidesOnNormalizedEmail()
    {
        var first = Sample("first@example.com");
        var second = Sample("second@example.com");
        await _store.CreateAsync(first, CancellationToken.None);
        await _store.CreateAsync(second, CancellationToken.None);

        second.Email = first.Email;
        second.NormalizedEmail = first.NormalizedEmail;

        var result = await _store.UpdateAsync(second, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "DuplicateEmail");
    }

    [Fact]
    public async Task Should_Throw_When_UpdateAsyncUserIsNull()
    {
        Func<Task> act = () => _store.UpdateAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------
    // DeleteAsync
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_RemoveUser_When_DeleteAsyncSucceeds()
    {
        var user = Sample("delete@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        var result = await _store.DeleteAsync(user, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task Should_ReturnConcurrencyFailure_When_DeleteAsyncStampDoesNotMatch()
    {
        var user = Sample("stale-delete@example.com");
        await _store.CreateAsync(user, CancellationToken.None);

        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        var result = await _store.DeleteAsync(user, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "ConcurrencyFailure");
    }

    [Fact]
    public async Task Should_Throw_When_DeleteAsyncUserIsNull()
    {
        Func<Task> act = () => _store.DeleteAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------
    // Property setters / getters (no DB hits)
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_SetAndGetPasswordHash_When_PasswordStoreMethodsCalled()
    {
        var user = Sample();
        user.PasswordHash = null;

        await _store.SetPasswordHashAsync(user, "hashed", CancellationToken.None);

        (await _store.GetPasswordHashAsync(user, CancellationToken.None)).Should().Be("hashed");
        (await _store.HasPasswordAsync(user, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_SetAndGetSecurityStamp_When_SecurityStampStoreMethodsCalled()
    {
        var user = Sample();
        const string stamp = "stamp-value";

        await _store.SetSecurityStampAsync(user, stamp, CancellationToken.None);

        (await _store.GetSecurityStampAsync(user, CancellationToken.None)).Should().Be(stamp);
    }

    [Fact]
    public async Task Should_SetAndGetEmailFlags_When_EmailStoreMethodsCalled()
    {
        var user = Sample();

        await _store.SetEmailAsync(user, "set@example.com", CancellationToken.None);
        await _store.SetNormalizedEmailAsync(user, "SET@EXAMPLE.COM", CancellationToken.None);
        await _store.SetEmailConfirmedAsync(user, confirmed: true, CancellationToken.None);

        (await _store.GetEmailAsync(user, CancellationToken.None)).Should().Be("set@example.com");
        (await _store.GetNormalizedEmailAsync(user, CancellationToken.None)).Should().Be("SET@EXAMPLE.COM");
        (await _store.GetEmailConfirmedAsync(user, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_SetAndGetLockoutFields_When_LockoutStoreMethodsCalled()
    {
        var user = Sample();
        var until = DateTimeOffset.UtcNow.AddHours(1);

        await _store.SetLockoutEndDateAsync(user, until, CancellationToken.None);
        (await _store.GetLockoutEndDateAsync(user, CancellationToken.None)).Should().Be(until);

        (await _store.IncrementAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(1);
        (await _store.GetAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(1);

        await _store.ResetAccessFailedCountAsync(user, CancellationToken.None);
        (await _store.GetAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(0);

        await _store.SetLockoutEnabledAsync(user, enabled: false, CancellationToken.None);
        (await _store.GetLockoutEnabledAsync(user, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_ReturnEmail_When_GetUserNameAsyncCalled()
    {
        var user = Sample();

        await _store.SetUserNameAsync(user, "name@example.com", CancellationToken.None);
        await _store.SetNormalizedUserNameAsync(user, "NAME@EXAMPLE.COM", CancellationToken.None);

        (await _store.GetUserNameAsync(user, CancellationToken.None)).Should().Be("name@example.com");
        (await _store.GetNormalizedUserNameAsync(user, CancellationToken.None)).Should().Be("NAME@EXAMPLE.COM");
        (await _store.GetUserIdAsync(user, CancellationToken.None)).Should().Be(user.Id.ToString());
    }

    /// <summary>
    /// Exercises every public method that takes a <see cref="HeimdallUser"/> as its
    /// first argument to assert it raises <see cref="ArgumentNullException"/> when
    /// passed <c>null</c>. Acts as a single guardrail for the
    /// <c>ArgumentNullException.ThrowIfNull(user)</c> pattern repeated across the store.
    /// </summary>
    /// <param name="methodName">The name of the store method under test.</param>
    [Theory]
    [InlineData(nameof(HeimdallUserStore.GetUserIdAsync))]
    [InlineData(nameof(HeimdallUserStore.GetUserNameAsync))]
    [InlineData(nameof(HeimdallUserStore.SetUserNameAsync))]
    [InlineData(nameof(HeimdallUserStore.GetNormalizedUserNameAsync))]
    [InlineData(nameof(HeimdallUserStore.SetNormalizedUserNameAsync))]
    [InlineData(nameof(HeimdallUserStore.SetPasswordHashAsync))]
    [InlineData(nameof(HeimdallUserStore.GetPasswordHashAsync))]
    [InlineData(nameof(HeimdallUserStore.HasPasswordAsync))]
    [InlineData(nameof(HeimdallUserStore.SetEmailAsync))]
    [InlineData(nameof(HeimdallUserStore.GetEmailAsync))]
    [InlineData(nameof(HeimdallUserStore.GetEmailConfirmedAsync))]
    [InlineData(nameof(HeimdallUserStore.SetEmailConfirmedAsync))]
    [InlineData(nameof(HeimdallUserStore.GetNormalizedEmailAsync))]
    [InlineData(nameof(HeimdallUserStore.SetNormalizedEmailAsync))]
    [InlineData(nameof(HeimdallUserStore.SetSecurityStampAsync))]
    [InlineData(nameof(HeimdallUserStore.GetSecurityStampAsync))]
    [InlineData(nameof(HeimdallUserStore.GetLockoutEndDateAsync))]
    [InlineData(nameof(HeimdallUserStore.SetLockoutEndDateAsync))]
    [InlineData(nameof(HeimdallUserStore.IncrementAccessFailedCountAsync))]
    [InlineData(nameof(HeimdallUserStore.ResetAccessFailedCountAsync))]
    [InlineData(nameof(HeimdallUserStore.GetAccessFailedCountAsync))]
    [InlineData(nameof(HeimdallUserStore.GetLockoutEnabledAsync))]
    [InlineData(nameof(HeimdallUserStore.SetLockoutEnabledAsync))]
    public async Task Should_Throw_When_PublicMethodsGivenNullUser(string methodName)
    {
        Func<Task> act = methodName switch
        {
            nameof(HeimdallUserStore.GetUserIdAsync) => () => _store.GetUserIdAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.GetUserNameAsync) => () => _store.GetUserNameAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetUserNameAsync) => () => _store.SetUserNameAsync(null!, "x", CancellationToken.None),
            nameof(HeimdallUserStore.GetNormalizedUserNameAsync) => () => _store.GetNormalizedUserNameAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetNormalizedUserNameAsync) => () => _store.SetNormalizedUserNameAsync(null!, "x", CancellationToken.None),
            nameof(HeimdallUserStore.SetPasswordHashAsync) => () => _store.SetPasswordHashAsync(null!, "x", CancellationToken.None),
            nameof(HeimdallUserStore.GetPasswordHashAsync) => () => _store.GetPasswordHashAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.HasPasswordAsync) => () => _store.HasPasswordAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetEmailAsync) => () => _store.SetEmailAsync(null!, "x", CancellationToken.None),
            nameof(HeimdallUserStore.GetEmailAsync) => () => _store.GetEmailAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.GetEmailConfirmedAsync) => () => _store.GetEmailConfirmedAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetEmailConfirmedAsync) => () => _store.SetEmailConfirmedAsync(null!, true, CancellationToken.None),
            nameof(HeimdallUserStore.GetNormalizedEmailAsync) => () => _store.GetNormalizedEmailAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetNormalizedEmailAsync) => () => _store.SetNormalizedEmailAsync(null!, "x", CancellationToken.None),
            nameof(HeimdallUserStore.SetSecurityStampAsync) => () => _store.SetSecurityStampAsync(null!, "x", CancellationToken.None),
            nameof(HeimdallUserStore.GetSecurityStampAsync) => () => _store.GetSecurityStampAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.GetLockoutEndDateAsync) => () => _store.GetLockoutEndDateAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetLockoutEndDateAsync) => () => _store.SetLockoutEndDateAsync(null!, null, CancellationToken.None),
            nameof(HeimdallUserStore.IncrementAccessFailedCountAsync) => () => _store.IncrementAccessFailedCountAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.ResetAccessFailedCountAsync) => () => _store.ResetAccessFailedCountAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.GetAccessFailedCountAsync) => () => _store.GetAccessFailedCountAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.GetLockoutEnabledAsync) => () => _store.GetLockoutEnabledAsync(null!, CancellationToken.None),
            nameof(HeimdallUserStore.SetLockoutEnabledAsync) => () => _store.SetLockoutEnabledAsync(null!, true, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unmapped method '{methodName}'."),
        };

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_HonourCancellation_When_CreateAsyncTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _store.CreateAsync(Sample("cancel@example.com"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // -----------------------------------------------------------------------------
    // DI registration
    // -----------------------------------------------------------------------------

    [Fact]
    public void Should_ResolveSameInstance_When_AllFiveIdentityInterfacesResolvedFromScope()
    {
        var services = new ServiceCollection();
        services.Configure<DataOptions>(o => o.PostgresConnectionString = _fx.ConnectionString);
        services.AddHeimdallIdentityStores();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HeimdallUser>>();
        var passwordStore = scope.ServiceProvider.GetRequiredService<IUserPasswordStore<HeimdallUser>>();
        var emailStore = scope.ServiceProvider.GetRequiredService<IUserEmailStore<HeimdallUser>>();
        var securityStore = scope.ServiceProvider.GetRequiredService<IUserSecurityStampStore<HeimdallUser>>();
        var lockoutStore = scope.ServiceProvider.GetRequiredService<IUserLockoutStore<HeimdallUser>>();

        ReferenceEquals(userStore, passwordStore).Should().BeTrue();
        ReferenceEquals(userStore, emailStore).Should().BeTrue();
        ReferenceEquals(userStore, securityStore).Should().BeTrue();
        ReferenceEquals(userStore, lockoutStore).Should().BeTrue();
    }
}

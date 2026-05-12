using System.Collections.Concurrent;
using Dapper;
using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Identity;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Identity;

/// <summary>
/// Integration tests for the Phase 4.2 two-factor surface on
/// <see cref="HeimdallUserStore"/> — <see cref="IUserTwoFactorStore{TUser}"/>,
/// <see cref="IUserAuthenticatorKeyStore{TUser}"/> and
/// <see cref="IUserTwoFactorRecoveryCodeStore{TUser}"/>. Tests run against the
/// real Postgres container provided by <see cref="PostgresFixture"/>, with the
/// MFA + users tables reset before each test for determinism. The store is
/// usually cast to the relevant interface in the test body so the
/// interface-shaped overloads are exercised the same way Identity invokes them.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class HeimdallUserStoreMfaTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly HeimdallUserStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeimdallUserStoreMfaTests"/> class.
    /// </summary>
    /// <param name="fx">The shared Postgres fixture.</param>
    public HeimdallUserStoreMfaTests(PostgresFixture fx)
    {
        _fx = fx;
        var options = Options.Create(new DataOptions { PostgresConnectionString = fx.ConnectionString });
        _store = new HeimdallUserStore(options, new PasswordHasher<HeimdallUser>());
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fx.ResetUsersTableAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Builds a fresh <see cref="HeimdallUser"/> with the supplied email (and the same
    /// upper-cased value as <c>NormalizedEmail</c>) plus reasonable defaults for the
    /// remaining required columns. Mirrors the helper in
    /// <c>HeimdallUserStoreTests</c> so the fixtures are interchangeable.
    /// </summary>
    /// <param name="email">The plain (case-preserved) email address.</param>
    /// <returns>A fully-populated <see cref="HeimdallUser"/> ready to insert.</returns>
    private static HeimdallUser Sample(string email = "mfa@example.com")
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

    private async Task<HeimdallUser> CreatePersistedAsync(string email)
    {
        var user = Sample(email);
        var result = await _store.CreateAsync(user, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        return user;
    }

    // ---------------------------------------------------------------------------------
    // IUserTwoFactorStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_DefaultToFalse_When_NewUserCreated()
    {
        var user = await CreatePersistedAsync("tfa-default@example.com");
        var twoFactor = (IUserTwoFactorStore<HeimdallUser>)_store;

        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        fetched.Should().NotBeNull();

        var enabled = await twoFactor.GetTwoFactorEnabledAsync(fetched!, CancellationToken.None);
        enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Should_PersistTrue_When_SetThenUpdate()
    {
        var user = await CreatePersistedAsync("tfa-enable@example.com");
        var twoFactor = (IUserTwoFactorStore<HeimdallUser>)_store;

        await twoFactor.SetTwoFactorEnabledAsync(user, true, CancellationToken.None);
        user.TwoFactorEnabled.Should().BeTrue();

        var update = await _store.UpdateAsync(user, CancellationToken.None);
        update.Succeeded.Should().BeTrue();

        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.TwoFactorEnabled.Should().BeTrue();
        (await twoFactor.GetTwoFactorEnabledAsync(fetched, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_PersistFalse_When_SetFalseThenUpdate()
    {
        var user = await CreatePersistedAsync("tfa-disable@example.com");
        var twoFactor = (IUserTwoFactorStore<HeimdallUser>)_store;

        await twoFactor.SetTwoFactorEnabledAsync(user, true, CancellationToken.None);
        (await _store.UpdateAsync(user, CancellationToken.None)).Succeeded.Should().BeTrue();

        await twoFactor.SetTwoFactorEnabledAsync(user, false, CancellationToken.None);
        user.TwoFactorEnabled.Should().BeFalse();
        (await _store.UpdateAsync(user, CancellationToken.None)).Succeeded.Should().BeTrue();

        var fetched = await _store.FindByIdAsync(user.Id.ToString(), CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.TwoFactorEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Throw_When_TwoFactorUserIsNull()
    {
        var twoFactor = (IUserTwoFactorStore<HeimdallUser>)_store;

        Func<Task> get = () => twoFactor.GetTwoFactorEnabledAsync(null!, CancellationToken.None);
        Func<Task> set = () => twoFactor.SetTwoFactorEnabledAsync(null!, true, CancellationToken.None);

        await get.Should().ThrowAsync<ArgumentNullException>();
        await set.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------------------------------------------------------------------------------
    // IUserAuthenticatorKeyStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ReturnNull_When_NoKeyStored()
    {
        var user = await CreatePersistedAsync("key-empty@example.com");
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;

        var key = await keys.GetAuthenticatorKeyAsync(user, CancellationToken.None);
        key.Should().BeNull();
    }

    [Fact]
    public async Task Should_RoundTripKey_When_SetThenGet()
    {
        var user = await CreatePersistedAsync("key-roundtrip@example.com");
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;

        await keys.SetAuthenticatorKeyAsync(user, "JBSWY3DPEHPK3PXP", CancellationToken.None);

        var fetched = await keys.GetAuthenticatorKeyAsync(user, CancellationToken.None);
        fetched.Should().Be("JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public async Task Should_Replace_When_SetTwice()
    {
        var user = await CreatePersistedAsync("key-replace@example.com");
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;

        await keys.SetAuthenticatorKeyAsync(user, "FIRSTKEY", CancellationToken.None);
        await keys.SetAuthenticatorKeyAsync(user, "SECONDKEY", CancellationToken.None);

        var fetched = await keys.GetAuthenticatorKeyAsync(user, CancellationToken.None);
        fetched.Should().Be("SECONDKEY");

        // A direct Dapper count proves the replace is idempotent — the table
        // never accumulates rows under the (user_id, provider_name) key.
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM user_authenticator_keys WHERE user_id = @UserId;",
            new { UserId = user.Id });
        count.Should().Be(1);
    }

    [Fact]
    public async Task Should_Throw_When_AuthenticatorKeyUserIsNull_OnGet()
    {
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;
        Func<Task> act = () => keys.GetAuthenticatorKeyAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_AuthenticatorKeyUserIsNull_OnSet()
    {
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;
        Func<Task> act = () => keys.SetAuthenticatorKeyAsync(null!, "KEY", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_AuthenticatorKeyIsNull()
    {
        var user = await CreatePersistedAsync("key-null@example.com");
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;
        Func<Task> act = () => keys.SetAuthenticatorKeyAsync(user, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_CascadeDelete_When_UserDeletedWithAuthenticatorKey()
    {
        var user = await CreatePersistedAsync("key-cascade@example.com");
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;

        await keys.SetAuthenticatorKeyAsync(user, "TOBEDELETED", CancellationToken.None);

        var delete = await _store.DeleteAsync(user, CancellationToken.None);
        delete.Succeeded.Should().BeTrue();

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM user_authenticator_keys WHERE user_id = @UserId;",
            new { UserId = user.Id });
        count.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------
    // IUserTwoFactorRecoveryCodeStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_ReturnZero_When_CountWithNoCodes()
    {
        var user = await CreatePersistedAsync("rc-empty@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        (await codes.CountCodesAsync(user, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Should_StoreHashedCodes_When_ReplaceCodes()
    {
        var user = await CreatePersistedAsync("rc-hash@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        await codes.ReplaceCodesAsync(user, new[] { "code-1", "code-2" }, CancellationToken.None);

        (await codes.CountCodesAsync(user, CancellationToken.None)).Should().Be(2);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var hashes = (await conn.QueryAsync<string>(
            "SELECT code_hash FROM user_recovery_codes WHERE user_id = @UserId;",
            new { UserId = user.Id })).ToList();

        hashes.Should().HaveCount(2);
        hashes.Should().NotContain("code-1");
        hashes.Should().NotContain("code-2");
        // Identity's PBKDF2 hasher produces base64 payloads that are far longer
        // than either plaintext — a defensive length guard catches any future
        // regression that silently switches to a plaintext column.
        hashes.Should().OnlyContain(h => h.Length > "code-1".Length + 8);
    }

    [Fact]
    public async Task Should_RedeemAndDecrement_When_ValidCodeProvided()
    {
        var user = await CreatePersistedAsync("rc-redeem@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        await codes.ReplaceCodesAsync(user, new[] { "code-1", "code-2" }, CancellationToken.None);

        (await codes.RedeemCodeAsync(user, "code-1", CancellationToken.None)).Should().BeTrue();
        (await codes.CountCodesAsync(user, CancellationToken.None)).Should().Be(1);

        // A second redemption of the same plaintext must fail — the matching
        // row is now flagged used_at IS NOT NULL and is excluded from the
        // SELECT … FOR UPDATE candidate set.
        (await codes.RedeemCodeAsync(user, "code-1", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_UnknownCodeProvided()
    {
        var user = await CreatePersistedAsync("rc-unknown@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        await codes.ReplaceCodesAsync(user, new[] { "code-1" }, CancellationToken.None);

        (await codes.RedeemCodeAsync(user, "not-a-code", CancellationToken.None)).Should().BeFalse();
        (await codes.CountCodesAsync(user, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task Should_ReplaceAllCodes_When_ReplaceCalledTwice()
    {
        var user = await CreatePersistedAsync("rc-replace@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        await codes.ReplaceCodesAsync(user, new[] { "old-1", "old-2", "old-3" }, CancellationToken.None);
        await codes.ReplaceCodesAsync(user, new[] { "new-1" }, CancellationToken.None);

        (await codes.CountCodesAsync(user, CancellationToken.None)).Should().Be(1);
        (await codes.RedeemCodeAsync(user, "old-1", CancellationToken.None)).Should().BeFalse();
        (await codes.RedeemCodeAsync(user, "new-1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Should_ReturnFalse_When_CodeRedeemedConcurrently()
    {
        // The FOR UPDATE row-lock + WHERE used_at IS NULL predicate together
        // guarantee at most one winner. Looping over a handful of iterations
        // makes the race reliable on fast CI hardware while still completing
        // in well under two seconds.
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        for (int i = 0; i < 8; i++)
        {
            await _fx.ResetUsersTableAsync();
            var user = await CreatePersistedAsync($"rc-race-{i}@example.com");

            await codes.ReplaceCodesAsync(user, new[] { "only-code" }, CancellationToken.None);

            // Two independent stores against the same connection string —
            // Dapper opens a fresh NpgsqlConnection per call so the two redeem
            // operations land in separate Postgres transactions, which is the
            // race we want to exercise.
            var optionsA = Options.Create(new DataOptions { PostgresConnectionString = _fx.ConnectionString });
            var optionsB = Options.Create(new DataOptions { PostgresConnectionString = _fx.ConnectionString });
            var storeA = new HeimdallUserStore(optionsA, new PasswordHasher<HeimdallUser>());
            var storeB = new HeimdallUserStore(optionsB, new PasswordHasher<HeimdallUser>());

            var taskA = ((IUserTwoFactorRecoveryCodeStore<HeimdallUser>)storeA)
                .RedeemCodeAsync(user, "only-code", CancellationToken.None);
            var taskB = ((IUserTwoFactorRecoveryCodeStore<HeimdallUser>)storeB)
                .RedeemCodeAsync(user, "only-code", CancellationToken.None);

            var results = await Task.WhenAll(taskA, taskB);
            results.Count(r => r).Should().Be(1, "exactly one redemption must win the race");
            results.Count(r => !r).Should().Be(1, "the loser must observe used_at IS NOT NULL");
            (await codes.CountCodesAsync(user, CancellationToken.None)).Should().Be(0);
        }
    }

    [Fact]
    public async Task Should_Throw_When_RecoveryCodeUserIsNull()
    {
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        Func<Task> replace = () => codes.ReplaceCodesAsync(null!, Array.Empty<string>(), CancellationToken.None);
        Func<Task> redeem = () => codes.RedeemCodeAsync(null!, "x", CancellationToken.None);
        Func<Task> count = () => codes.CountCodesAsync(null!, CancellationToken.None);

        await replace.Should().ThrowAsync<ArgumentNullException>();
        await redeem.Should().ThrowAsync<ArgumentNullException>();
        await count.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_RecoveryCodeIsNull()
    {
        var user = await CreatePersistedAsync("rc-null-code@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        Func<Task> act = () => codes.RedeemCodeAsync(user, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_RecoveryCodesEnumerableIsNull()
    {
        var user = await CreatePersistedAsync("rc-null-enum@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        Func<Task> act = () => codes.ReplaceCodesAsync(user, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_Throw_When_RecoveryCodeEnumerableContainsNullElement()
    {
        var user = await CreatePersistedAsync("rc-null-elem@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        Func<Task> act = () => codes.ReplaceCodesAsync(user, new string[] { "ok", null! }, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Should_CascadeDelete_When_UserDeletedWithRecoveryCodes()
    {
        var user = await CreatePersistedAsync("rc-cascade@example.com");
        var codes = (IUserTwoFactorRecoveryCodeStore<HeimdallUser>)_store;

        await codes.ReplaceCodesAsync(user, new[] { "a", "b", "c" }, CancellationToken.None);

        var delete = await _store.DeleteAsync(user, CancellationToken.None);
        delete.Succeeded.Should().BeTrue();

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM user_recovery_codes WHERE user_id = @UserId;",
            new { UserId = user.Id });
        count.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------
    // Logging-hygiene (Phase 4.2 checklist step 2): the authenticator key must
    // never be observed in application logs. The store does not currently take
    // an ILogger, so a captor-based assertion here would be vacuous — the
    // captured message bag is guaranteed empty regardless of store behaviour
    // (flagged by both the PR reviewer and the security reviewer on PR #44).
    // The test is deferred until MFA-related logging actually lands; the
    // [Fact(Skip = …)] keeps the invariant visible in the suite so a future
    // contributor adding an ILogger to HeimdallUserStore has an obvious hook
    // to re-enable it.
    // ---------------------------------------------------------------------------------

    [Fact(Skip = "Deferred: HeimdallUserStore has no ILogger yet, so the captor is vacuous. Re-enable when MFA-related logging is added (PR #44 reviewer + security-reviewer feedback).")]
    public async Task Should_NotLogAuthenticatorKey_When_SetThenGet()
    {
        const string Secret = "SECRET-KEY-VALUE-123";

        using var captor = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(captor);
        });
        // Resolving a logger keyed to the store ensures a future ILogger
        // injection points at the same factory the captor observes.
        _ = loggerFactory.CreateLogger<HeimdallUserStore>();

        var user = await CreatePersistedAsync("log-hygiene@example.com");
        var keys = (IUserAuthenticatorKeyStore<HeimdallUser>)_store;

        await keys.SetAuthenticatorKeyAsync(user, Secret, CancellationToken.None);
        var fetched = await keys.GetAuthenticatorKeyAsync(user, CancellationToken.None);

        fetched.Should().Be(Secret);
        captor.Messages.Should().NotContain(m => m.Contains(Secret, StringComparison.Ordinal),
            "the authenticator shared secret is defence-in-depth never permitted in application logs");
    }

    /// <summary>
    /// Minimal in-process <see cref="ILoggerProvider"/> that appends every
    /// formatted log message to a thread-safe bag for later inspection. Only
    /// used by the logging-hygiene test above.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;

            public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (formatter is null)
                {
                    return;
                }

                _owner.Messages.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }
}

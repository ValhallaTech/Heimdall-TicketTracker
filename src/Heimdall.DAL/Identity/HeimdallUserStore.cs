using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Identity;

/// <summary>
/// Dapper-backed ASP.NET Core Identity store for <see cref="HeimdallUser"/>. Implements
/// the Authenticated Foundation surface (Phase 1 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3) — user CRUD, password,
/// email, security-stamp, lockout — together with the Phase 4.1 two-factor stores
/// (<see cref="IUserTwoFactorStore{TUser}"/>, <see cref="IUserAuthenticatorKeyStore{TUser}"/>,
/// <see cref="IUserTwoFactorRecoveryCodeStore{TUser}"/>). Roles, claims, phone, and external
/// logins are intentionally out of scope and are not implemented.
/// </summary>
/// <remarks>
/// The store opens an <see cref="NpgsqlConnection"/> per call (mirroring
/// <c>TicketRepository</c>) — no connection is held by the instance, so
/// <see cref="Dispose"/> is a no-op. Optimistic concurrency is enforced via the
/// <c>concurrency_stamp</c> column on every <see cref="UpdateAsync"/> /
/// <see cref="DeleteAsync"/>. Duplicate email collisions are surfaced as the standard
/// Identity <c>DuplicateEmail</c> error by trapping Postgres SQLSTATE <c>23505</c>.
/// Authenticator-key and recovery-code mutations open short-lived transactions so the
/// "delete then insert" idempotency contract is atomic, and recovery-code redemption
/// uses <c>SELECT … FOR UPDATE</c> plus a conditional <c>UPDATE … WHERE used_at IS NULL</c>
/// so a code can only be redeemed once even under concurrent requests.
/// </remarks>
public sealed class HeimdallUserStore :
    IUserStore<HeimdallUser>,
    IUserPasswordStore<HeimdallUser>,
    IUserEmailStore<HeimdallUser>,
    IUserSecurityStampStore<HeimdallUser>,
    IUserLockoutStore<HeimdallUser>,
    IUserTwoFactorStore<HeimdallUser>,
    IUserAuthenticatorKeyStore<HeimdallUser>,
    IUserTwoFactorRecoveryCodeStore<HeimdallUser>
{
    // Full column projection — used by every read path on the store. Aliased to the
    // PascalCase property names on HeimdallUser so Dapper materialises rows directly
    // without a custom column mapper.
    //
    // email and normalized_email are declared as citext in the schema (locked by the
    // §9.3 design — case-insensitive comparison happens at the column level). Npgsql 10
    // cannot resolve the citext OID through a raw NpgsqlConnection built from a plain
    // connection string (no DataSource-based extension-type registration), so reading
    // a citext field directly into string via Dapper throws InvalidCastException.
    // Casting to text in the projection — Postgres auto-casts citext → text on output —
    // sidesteps the reader without changing schema, DI, or the public API. INSERT /
    // UPDATE / WHERE-clause sides are unaffected: text literals bind implicitly to
    // citext columns, preserving case-insensitive lookups.
    private const string SelectColumns =
        "id AS Id, email::text AS Email, normalized_email::text AS NormalizedEmail, "
        + "password_hash AS PasswordHash, security_stamp AS SecurityStamp, "
        + "concurrency_stamp AS ConcurrencyStamp, email_confirmed AS EmailConfirmed, "
        + "lockout_end AS LockoutEnd, lockout_enabled AS LockoutEnabled, "
        + "access_failed_count AS AccessFailedCount, system_admin AS SystemAdmin, "
        + "two_factor_enabled AS TwoFactorEnabled, "
        + "created_at AS CreatedAt, updated_at AS UpdatedAt";

    // Postgres SQLSTATE for unique-violation. The users table has unique indexes on
    // both email and normalized_email; either collision maps to the standard Identity
    // DuplicateEmail error code.
    private const string UniqueViolationSqlState = "23505";

    // Authenticator provider discriminator stored in user_authenticator_keys.provider_name.
    // Identity's TOTP token provider is registered under this name (see
    // TokenOptions.DefaultAuthenticatorProvider), so the store filters on the same literal.
    private const string AuthenticatorProviderName = "Authenticator";

    private readonly string _connectionString;
    private readonly IPasswordHasher<HeimdallUser> _passwordHasher;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeimdallUserStore"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <param name="passwordHasher">
    /// Identity password hasher used to hash recovery codes on
    /// <see cref="ReplaceCodesAsync(HeimdallUser, IEnumerable{string}, CancellationToken)"/>
    /// and to verify them on
    /// <see cref="RedeemCodeAsync(HeimdallUser, string, CancellationToken)"/>. The hasher
    /// already attaches a per-hash salt internally, so codes never need to be compared
    /// with <c>==</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="passwordHasher"/> is <c>null</c>.
    /// </exception>
    public HeimdallUserStore(IOptions<DataOptions> options, IPasswordHasher<HeimdallUser> passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        _connectionString = options.Value.PostgresConnectionString;
        _passwordHasher = passwordHasher;
    }

    private NpgsqlConnection CreateConnection() => new(_connectionString);

    // ---------------------------------------------------------------------------------
    // IUserStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<string> GetUserIdAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.Id.ToString());
    }

    /// <inheritdoc />
    public Task<string?> GetUserNameAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.Email);
    }

    /// <inheritdoc />
    public Task SetUserNameAsync(HeimdallUser user, string? userName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.Email = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedUserNameAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.NormalizedEmail);
    }

    /// <inheritdoc />
    public Task SetNormalizedUserNameAsync(HeimdallUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.NormalizedEmail = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IdentityResult> CreateAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        // created_at / updated_at are sourced from the database clock via now() to stay
        // consistent with TicketRepository.UpdateAsync — avoids client-clock skew.
        const string sql = @"
INSERT INTO users
    (id, email, normalized_email, password_hash, security_stamp, concurrency_stamp,
     email_confirmed, lockout_end, lockout_enabled, access_failed_count, system_admin,
     two_factor_enabled, created_at, updated_at)
VALUES
    (@Id, @Email, @NormalizedEmail, @PasswordHash, @SecurityStamp, @ConcurrencyStamp,
     @EmailConfirmed, @LockoutEnd, @LockoutEnabled, @AccessFailedCount, @SystemAdmin,
     @TwoFactorEnabled, now(), now());";

        await using var connection = CreateConnection();
        var command = new CommandDefinition(sql, user, cancellationToken: cancellationToken);

        try
        {
            await connection.ExecuteAsync(command).ConfigureAwait(false);
            return IdentityResult.Success;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolationSqlState)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateEmail",
                Description = "Email is already taken.",
            });
        }
    }

    /// <inheritdoc />
    public async Task<IdentityResult> UpdateAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        // Optimistic concurrency: the WHERE clause asserts the stamp the caller loaded
        // is still current; the SET clause rotates it to a fresh value. Zero rows
        // affected means the row was modified or deleted by a concurrent transaction.
        string newConcurrencyStamp = Guid.NewGuid().ToString();
        string currentConcurrencyStamp = user.ConcurrencyStamp;

        const string sql = @"
UPDATE users SET
    email                = @Email,
    normalized_email     = @NormalizedEmail,
    password_hash        = @PasswordHash,
    security_stamp       = @SecurityStamp,
    concurrency_stamp    = @NewConcurrencyStamp,
    email_confirmed      = @EmailConfirmed,
    lockout_end          = @LockoutEnd,
    lockout_enabled      = @LockoutEnabled,
    access_failed_count  = @AccessFailedCount,
    system_admin         = @SystemAdmin,
    two_factor_enabled   = @TwoFactorEnabled,
    updated_at           = now()
WHERE id = @Id
  AND concurrency_stamp = @CurrentConcurrencyStamp;";

        var parameters = new DynamicParameters();
        parameters.Add("Id", user.Id);
        parameters.Add("Email", user.Email);
        parameters.Add("NormalizedEmail", user.NormalizedEmail);
        parameters.Add("PasswordHash", user.PasswordHash);
        parameters.Add("SecurityStamp", user.SecurityStamp);
        parameters.Add("NewConcurrencyStamp", newConcurrencyStamp);
        parameters.Add("CurrentConcurrencyStamp", currentConcurrencyStamp);
        parameters.Add("EmailConfirmed", user.EmailConfirmed);
        parameters.Add("LockoutEnd", user.LockoutEnd);
        parameters.Add("LockoutEnabled", user.LockoutEnabled);
        parameters.Add("AccessFailedCount", user.AccessFailedCount);
        parameters.Add("SystemAdmin", user.SystemAdmin);
        parameters.Add("TwoFactorEnabled", user.TwoFactorEnabled);

        await using var connection = CreateConnection();
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);

        try
        {
            int rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
            if (rows == 0)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "ConcurrencyFailure",
                    Description = "Optimistic concurrency failure, object has been modified.",
                });
            }

            user.ConcurrencyStamp = newConcurrencyStamp;
            return IdentityResult.Success;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolationSqlState)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateEmail",
                Description = "Email is already taken.",
            });
        }
    }

    /// <inheritdoc />
    public async Task<IdentityResult> DeleteAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        const string sql = "DELETE FROM users WHERE id = @Id AND concurrency_stamp = @ConcurrencyStamp;";

        await using var connection = CreateConnection();
        var command = new CommandDefinition(
            sql,
            new { user.Id, user.ConcurrencyStamp },
            cancellationToken: cancellationToken);

        int rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        if (rows == 0)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "ConcurrencyFailure",
                Description = "Optimistic concurrency failure, object has been modified.",
            });
        }

        return IdentityResult.Success;
    }

    /// <inheritdoc />
    public async Task<HeimdallUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        cancellationToken.ThrowIfCancellationRequested();

        // Identity hands us the user id as a string — convert to Guid up front so an
        // invalid id surfaces here as a null result rather than as an opaque Postgres
        // cast error from the database.
        if (!Guid.TryParse(userId, out Guid id))
        {
            return null;
        }

        await using var connection = CreateConnection();
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM users WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<HeimdallUser>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<HeimdallUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        // For Heimdall, "user name" === "email", so the lookup uses the normalized_email
        // column — same as FindByEmailAsync.
        return FindByEmailAsync(normalizedUserName, cancellationToken);
    }

    // ---------------------------------------------------------------------------------
    // IUserPasswordStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task SetPasswordHashAsync(HeimdallUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetPasswordHashAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.PasswordHash);
    }

    /// <inheritdoc />
    public Task<bool> HasPasswordAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    // ---------------------------------------------------------------------------------
    // IUserEmailStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task SetEmailAsync(HeimdallUser user, string? email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.Email = email ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetEmailAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.Email);
    }

    /// <inheritdoc />
    public Task<bool> GetEmailConfirmedAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.EmailConfirmed);
    }

    /// <inheritdoc />
    public Task SetEmailConfirmedAsync(HeimdallUser user, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<HeimdallUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = CreateConnection();
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM users WHERE normalized_email = @NormalizedEmail",
            new { NormalizedEmail = normalizedEmail },
            cancellationToken: cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<HeimdallUser>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedEmailAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.NormalizedEmail);
    }

    /// <inheritdoc />
    public Task SetNormalizedEmailAsync(HeimdallUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.NormalizedEmail = normalizedEmail ?? string.Empty;
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------
    // IUserSecurityStampStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task SetSecurityStampAsync(HeimdallUser user, string stamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(stamp);
        cancellationToken.ThrowIfCancellationRequested();
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetSecurityStampAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.SecurityStamp);
    }

    // ---------------------------------------------------------------------------------
    // IUserLockoutStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.LockoutEnd);
    }

    /// <inheritdoc />
    public Task SetLockoutEndDateAsync(HeimdallUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> IncrementAccessFailedCountAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.AccessFailedCount += 1;
        return Task.FromResult(user.AccessFailedCount);
    }

    /// <inheritdoc />
    public Task ResetAccessFailedCountAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> GetAccessFailedCountAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.AccessFailedCount);
    }

    /// <inheritdoc />
    public Task<bool> GetLockoutEnabledAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.LockoutEnabled);
    }

    /// <inheritdoc />
    public Task SetLockoutEnabledAsync(HeimdallUser user, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------
    // IUserTwoFactorStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Mutates the in-memory <paramref name="user"/> only — Identity persists the change
    /// by calling <see cref="UpdateAsync"/> afterwards, which round-trips the
    /// <c>two_factor_enabled</c> column. This matches the existing semantics of
    /// <see cref="SetPasswordHashAsync"/> and the other setters on this store.
    /// </remarks>
    public Task SetTwoFactorEnabledAsync(HeimdallUser user, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> GetTwoFactorEnabledAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.TwoFactorEnabled);
    }

    // ---------------------------------------------------------------------------------
    // IUserAuthenticatorKeyStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Persists the authenticator shared secret for <paramref name="user"/>, replacing
    /// any existing key. Idempotent via a single-transaction
    /// <c>DELETE</c> + <c>INSERT</c> on <c>user_authenticator_keys</c> keyed on
    /// <c>user_id</c> with <c>provider_name = 'Authenticator'</c>.
    /// </summary>
    /// <param name="user">The user whose authenticator key is being set. Must not be <c>null</c>.</param>
    /// <param name="key">The base32 shared secret to store verbatim. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the key has been persisted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> or <paramref name="key"/> is <c>null</c>.</exception>
    public async Task SetAuthenticatorKeyAsync(HeimdallUser user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var deleteCommand = new CommandDefinition(
            "DELETE FROM user_authenticator_keys WHERE user_id = @UserId;",
            new { UserId = user.Id },
            transaction: transaction,
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(deleteCommand).ConfigureAwait(false);

        var insertCommand = new CommandDefinition(
            @"INSERT INTO user_authenticator_keys (user_id, provider_name, authenticator_key)
              VALUES (@UserId, @ProviderName, @AuthenticatorKey);",
            new
            {
                UserId = user.Id,
                ProviderName = AuthenticatorProviderName,
                AuthenticatorKey = key,
            },
            transaction: transaction,
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(insertCommand).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the stored authenticator shared secret for <paramref name="user"/>, or
    /// <c>null</c> if no key has been registered.
    /// </summary>
    /// <param name="user">The user whose authenticator key is being read. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The stored base32 secret, or <c>null</c> when no row exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> is <c>null</c>.</exception>
    public async Task<string?> GetAuthenticatorKeyAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = CreateConnection();
        var command = new CommandDefinition(
            @"SELECT authenticator_key
              FROM user_authenticator_keys
              WHERE user_id = @UserId AND provider_name = @ProviderName;",
            new { UserId = user.Id, ProviderName = AuthenticatorProviderName },
            cancellationToken: cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<string?>(command).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------
    // IUserTwoFactorRecoveryCodeStore<HeimdallUser>
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Replaces every recovery code for <paramref name="user"/> with the supplied set.
    /// Codes are hashed via the injected <see cref="IPasswordHasher{TUser}"/> before
    /// insert — the table never stores plaintext or reversibly-encrypted codes. The
    /// delete + inserts run in a single transaction so observers never see a partial
    /// rotation.
    /// </summary>
    /// <param name="user">The user whose recovery codes are being replaced. Must not be <c>null</c>.</param>
    /// <param name="recoveryCodes">The fresh plaintext codes to hash and persist. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the codes have been persisted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> or <paramref name="recoveryCodes"/> is <c>null</c>.</exception>
    public async Task ReplaceCodesAsync(HeimdallUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var deleteCommand = new CommandDefinition(
            "DELETE FROM user_recovery_codes WHERE user_id = @UserId;",
            new { UserId = user.Id },
            transaction: transaction,
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(deleteCommand).ConfigureAwait(false);

        // IPasswordHasher.HashPassword already attaches a per-hash salt internally —
        // hashes of identical plaintext codes are not equal, which preserves the
        // "never equal-check a code-hash" invariant.
        foreach (string code in recoveryCodes)
        {
            ArgumentNullException.ThrowIfNull(code);
            string hash = _passwordHasher.HashPassword(user, code);

            var insertCommand = new CommandDefinition(
                @"INSERT INTO user_recovery_codes (user_id, code_hash)
                  VALUES (@UserId, @CodeHash);",
                new { UserId = user.Id, CodeHash = hash },
                transaction: transaction,
                cancellationToken: cancellationToken);
            await connection.ExecuteAsync(insertCommand).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to redeem the supplied recovery code. Unused hashes for the user are
    /// loaded under <c>SELECT … FOR UPDATE</c>; each is verified against the supplied
    /// code via <see cref="IPasswordHasher{TUser}.VerifyHashedPassword"/>. On the first
    /// match the row is marked used via a conditional <c>UPDATE … WHERE used_at IS NULL
    /// RETURNING id</c> so that concurrent attempts to redeem the same code resolve to
    /// at most one winner. Hashes are never compared with <c>==</c>.
    /// </summary>
    /// <param name="user">The user attempting redemption. Must not be <c>null</c>.</param>
    /// <param name="code">The plaintext recovery code supplied by the caller. Must not be <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when a code was redeemed; otherwise <c>false</c> (no matching unused
    /// code was found, or a concurrent transaction redeemed the matching row first).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> or <paramref name="code"/> is <c>null</c>.</exception>
    public async Task<bool> RedeemCodeAsync(HeimdallUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var selectCommand = new CommandDefinition(
            @"SELECT id AS Id, code_hash AS CodeHash
              FROM user_recovery_codes
              WHERE user_id = @UserId AND used_at IS NULL
              FOR UPDATE;",
            new { UserId = user.Id },
            transaction: transaction,
            cancellationToken: cancellationToken);

        IEnumerable<RecoveryCodeRow> rows = await connection.QueryAsync<RecoveryCodeRow>(selectCommand).ConfigureAwait(false);

        foreach (RecoveryCodeRow row in rows)
        {
            PasswordVerificationResult verification = _passwordHasher.VerifyHashedPassword(user, row.CodeHash, code);
            if (verification != PasswordVerificationResult.Success
                && verification != PasswordVerificationResult.SuccessRehashNeeded)
            {
                continue;
            }

            // Conditional UPDATE + RETURNING: the row-lock taken by SELECT … FOR UPDATE
            // plus the used_at IS NULL predicate together guarantee at most one redemption
            // per code even under concurrent transactions. A zero-row result means another
            // transaction won the race; surface that as a failure rather than a success.
            var updateCommand = new CommandDefinition(
                @"UPDATE user_recovery_codes
                  SET used_at = now()
                  WHERE id = @Id AND used_at IS NULL
                  RETURNING id;",
                new { Id = row.Id },
                transaction: transaction,
                cancellationToken: cancellationToken);

            Guid? redeemedId = await connection.QuerySingleOrDefaultAsync<Guid?>(updateCommand).ConfigureAwait(false);
            if (redeemedId is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <inheritdoc />
    public async Task<int> CountCodesAsync(HeimdallUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = CreateConnection();
        var command = new CommandDefinition(
            @"SELECT COUNT(*) FROM user_recovery_codes
              WHERE user_id = @UserId AND used_at IS NULL;",
            new { UserId = user.Id },
            cancellationToken: cancellationToken);

        return await connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Disposes the store. Connections are scoped per-call and not held by the instance,
    /// so this is intentionally a no-op — it satisfies the <see cref="IUserStore{TUser}"/>
    /// contract without releasing resources that don't exist.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty — no resources are held at the instance level.
    }

    /// <summary>
    /// Materialisation target for the recovery-code redemption SELECT. Private so the
    /// hash never escapes the store and cannot be logged or returned to callers.
    /// </summary>
    private sealed class RecoveryCodeRow
    {
        public Guid Id { get; set; }

        public string CodeHash { get; set; } = string.Empty;
    }
}

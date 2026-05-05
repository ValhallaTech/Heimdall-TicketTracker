using System;
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
/// the minimum surface area required by the Authenticated Foundation (Phase 1 of
/// <c>docs/proposals/security-and-authorization.md</c> §9.3): user CRUD, password,
/// email, security-stamp, and lockout. Roles, claims, two-factor, phone, and external
/// logins are intentionally out of scope and are not implemented.
/// </summary>
/// <remarks>
/// The store opens an <see cref="NpgsqlConnection"/> per call (mirroring
/// <c>TicketRepository</c>) — no connection is held by the instance, so
/// <see cref="Dispose"/> is a no-op. Optimistic concurrency is enforced via the
/// <c>concurrency_stamp</c> column on every <see cref="UpdateAsync"/> /
/// <see cref="DeleteAsync"/>. Duplicate email collisions are surfaced as the standard
/// Identity <c>DuplicateEmail</c> error by trapping Postgres SQLSTATE <c>23505</c>.
/// </remarks>
public sealed class HeimdallUserStore :
    IUserStore<HeimdallUser>,
    IUserPasswordStore<HeimdallUser>,
    IUserEmailStore<HeimdallUser>,
    IUserSecurityStampStore<HeimdallUser>,
    IUserLockoutStore<HeimdallUser>
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
        + "created_at AS CreatedAt, updated_at AS UpdatedAt";

    // Postgres SQLSTATE for unique-violation. The users table has unique indexes on
    // both email and normalized_email; either collision maps to the standard Identity
    // DuplicateEmail error code.
    private const string UniqueViolationSqlState = "23505";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeimdallUserStore"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public HeimdallUserStore(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
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
     created_at, updated_at)
VALUES
    (@Id, @Email, @NormalizedEmail, @PasswordHash, @SecurityStamp, @ConcurrencyStamp,
     @EmailConfirmed, @LockoutEnd, @LockoutEnabled, @AccessFailedCount, @SystemAdmin,
     now(), now());";

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
}

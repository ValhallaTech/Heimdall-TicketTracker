using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IRefreshTokenRepository"/>. Follows the
/// <see cref="SigningKeyRepository"/> pattern: one <see cref="NpgsqlConnection"/> per
/// call, <see cref="CommandDefinition"/> for cancellation, and
/// <c>ConfigureAwait(false)</c> on every library-side await. Unlike
/// <c>signing_keys</c>, <c>refresh_tokens</c> stores only PBKDF2 hashes so the
/// application connection talks to the table directly — no
/// <c>SECURITY DEFINER</c> indirection.
/// </summary>
public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private const string PublicSelectColumns =
        "id AS Id, user_id AS UserId, token_hash AS TokenHash, "
        + "family_id AS FamilyId, parent_id AS ParentId, replaced_by AS ReplacedBy, "
        + "issued_at AS IssuedAt, expires_at AS ExpiresAt, "
        + "revoked_at AS RevokedAt, revoked_reason AS RevokedReason";

    private const string InsertSql =
        "INSERT INTO refresh_tokens "
        + "(id, user_id, token_hash, family_id, parent_id, replaced_by, "
        + "issued_at, expires_at, revoked_at, revoked_reason) "
        + "VALUES (@Id, @UserId, @TokenHash, @FamilyId, @ParentId, @ReplacedBy, "
        + "@IssuedAt, @ExpiresAt, @RevokedAt, @RevokedReason);";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshTokenRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public RefreshTokenRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task InsertAsync(RefreshToken row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.TokenHash);
        ValidateRevokedReasonIfPresent(row.RevokedReason);

        var parameters = BuildInsertParameters(row);

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(InsertSql, parameters, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RefreshToken?> GetByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        string sql =
            $"SELECT {PublicSelectColumns} FROM refresh_tokens WHERE token_hash = @TokenHash;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RefreshTokenRow?>(command).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async Task<bool> RotateAsync(
        Guid oldId,
        RefreshToken newRow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newRow);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRow.TokenHash);
        ValidateRevokedReasonIfPresent(newRow.RevokedReason);

        // The UPDATE narrows on `revoked_at IS NULL` so a concurrent rotation (or a
        // replay attempt against an already-rotated row) yields zero affected rows.
        // RETURNING id lets us detect that case with a single round-trip via
        // QuerySingleOrDefaultAsync<Guid?>.
        //
        // Ordering note: we INSERT the new row BEFORE the UPDATE that sets the old
        // row's replaced_by to point at it. The replaced_by FK is not DEFERRABLE,
        // so it is checked at statement end — pointing replaced_by at a not-yet-
        // inserted id would fail with 23503. If the UPDATE then matches zero rows
        // (the rotation lost the race), the whole transaction is rolled back, which
        // also rolls back the speculative INSERT — the caller observes the same
        // "no successor inserted" semantics it would have observed under the
        // opposite ordering.
        const string rotateSql =
            "UPDATE refresh_tokens "
            + "SET revoked_at = now(), revoked_reason = 'rotated', replaced_by = @NewId "
            + "WHERE id = @OldId AND revoked_at IS NULL "
            + "RETURNING id;";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var insertCommand = new CommandDefinition(
            InsertSql,
            BuildInsertParameters(newRow),
            transaction: transaction,
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(insertCommand).ConfigureAwait(false);

        var updateCommand = new CommandDefinition(
            rotateSql,
            new { OldId = oldId, NewId = newRow.Id },
            transaction: transaction,
            cancellationToken: cancellationToken);
        var revokedId = await connection
            .QuerySingleOrDefaultAsync<Guid?>(updateCommand)
            .ConfigureAwait(false);

        if (revokedId is null)
        {
            // The row was already revoked — treat as a replay attempt. Roll back so
            // the speculatively-inserted successor row is dropped; the caller follows
            // up with RevokeFamilyAsync(family, "family_replay").
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> RevokeFamilyAsync(
        Guid familyId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!IsAllowedRevokedReason(reason))
        {
            // Explicit allow-list check: the column's CHECK constraint would also
            // catch this, but we refuse to send an unrecognised string to the DB
            // at all — that surface is the only place an unparameterised reason
            // string would otherwise be visible to Postgres.
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "reason must be one of RefreshTokenRevokedReason.Rotated, Logout, FamilyReplay, AdminRevoke.");
        }

        const string sql =
            "UPDATE refresh_tokens "
            + "SET revoked_at = now(), revoked_reason = @Reason "
            + "WHERE family_id = @FamilyId AND revoked_at IS NULL;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new { FamilyId = familyId, Reason = reason },
            cancellationToken: cancellationToken);
        return await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    private static bool IsAllowedRevokedReason(string reason) =>
        reason == RefreshTokenRevokedReason.Rotated
        || reason == RefreshTokenRevokedReason.Logout
        || reason == RefreshTokenRevokedReason.FamilyReplay
        || reason == RefreshTokenRevokedReason.AdminRevoke;

    private static void ValidateRevokedReasonIfPresent(string? reason)
    {
        if (reason is null)
        {
            return;
        }

        if (!IsAllowedRevokedReason(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "revoked_reason must be null or one of RefreshTokenRevokedReason.Rotated, Logout, FamilyReplay, AdminRevoke.");
        }
    }

    private static DynamicParameters BuildInsertParameters(RefreshToken row)
    {
        // Force DateTimeKind.Utc on every timestamp parameter (same precedent as
        // SigningKeyRepository.InsertAsync) so Npgsql binds them as UTC rather
        // than Unspecified — the columns are timestamptz and modern Npgsql
        // rejects Unspecified for those.
        var parameters = new DynamicParameters();
        parameters.Add("Id", row.Id);
        parameters.Add("UserId", row.UserId);
        parameters.Add("TokenHash", row.TokenHash);
        parameters.Add("FamilyId", row.FamilyId);
        parameters.Add("ParentId", row.ParentId);
        parameters.Add("ReplacedBy", row.ReplacedBy);
        parameters.Add("IssuedAt", DateTime.SpecifyKind(row.IssuedAt, DateTimeKind.Utc));
        parameters.Add("ExpiresAt", DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc));
        parameters.Add(
            "RevokedAt",
            row.RevokedAt is null
                ? (DateTime?)null
                : DateTime.SpecifyKind(row.RevokedAt.Value, DateTimeKind.Utc));
        parameters.Add("RevokedReason", row.RevokedReason);
        return parameters;
    }

    private static RefreshToken ToRecord(RefreshTokenRow row) =>
        new(
            row.Id,
            row.UserId,
            row.TokenHash,
            row.FamilyId,
            row.ParentId,
            row.ReplacedBy,
            DateTime.SpecifyKind(row.IssuedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc),
            row.RevokedAt is null
                ? null
                : DateTime.SpecifyKind(row.RevokedAt.Value, DateTimeKind.Utc),
            row.RevokedReason);

    /// <summary>
    /// Internal materialisation DTO; the public surface returns
    /// <see cref="RefreshToken"/> after the timestamps are pinned to
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    private sealed class RefreshTokenRow
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }

        public string TokenHash { get; set; } = string.Empty;

        public Guid FamilyId { get; set; }

        public Guid? ParentId { get; set; }

        public Guid? ReplacedBy { get; set; }

        public DateTime IssuedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? RevokedAt { get; set; }

        public string? RevokedReason { get; set; }
    }
}

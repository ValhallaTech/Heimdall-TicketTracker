using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Tokens;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ISigningKeyRepository"/>. Follows the
/// <see cref="OrganizationRepository"/> pattern: one <see cref="NpgsqlConnection"/> per
/// call, <see cref="CommandDefinition"/> for cancellation, and
/// <c>ConfigureAwait(false)</c> on every library-side await. Writes/reads of
/// <c>private_key_protected</c> go through the <c>SECURITY DEFINER</c> functions installed
/// in <see cref="Heimdall.DAL.Migrations.M202605130001_CreateSigningKeys"/>.
/// </summary>
public sealed class SigningKeyRepository : ISigningKeyRepository
{
    private const string PublicSelectColumns =
        "kid AS Kid, alg AS Alg, public_jwk::text AS PublicJwkJson, "
        + "not_before AS NotBefore, not_after AS NotAfter, retired_at AS RetiredAt, "
        + "created_at AS CreatedAt";

    private static readonly JsonSerializerOptions JwkJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SigningKeyRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public SigningKeyRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task InsertAsync(
        string kid,
        string alg,
        string publicJwkJson,
        byte[] privateKeyProtected,
        DateTime notBefore,
        DateTime notAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);
        ArgumentException.ThrowIfNullOrWhiteSpace(alg);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicJwkJson);
        ArgumentNullException.ThrowIfNull(privateKeyProtected);

        // The migration grants EXECUTE on this function to heimdall_app; the application
        // connection has no direct INSERT privilege on private_key_protected.
        const string sql =
            "SELECT signing_keys_insert(@Kid::text, @Alg::text, @PublicJwk::jsonb, "
            + "@PrivateKeyProtected::bytea, @NotBefore, @NotAfter);";

        // Bind via DynamicParameters for clarity even though every value here is a
        // type Dapper + Npgsql already round-trip correctly:
        //   * string  -> text     (cast to ::text / ::jsonb in the SQL above so the
        //                          SECURITY DEFINER function signature matches),
        //   * byte[]  -> bytea    (cast to ::bytea in the SQL),
        //   * DateTime (UtcKind) -> timestamptz.
        // We force DateTimeKind.Utc on the two timestamps so Npgsql binds them as UTC
        // rather than Unspecified (which would throw under modern Npgsql when the
        // column is timestamptz). No NpgsqlDbType is set explicitly — Dapper picks
        // the right one for each CLR type and the SQL-level casts pin the rest.
        var parameters = new DynamicParameters();
        parameters.Add("Kid", kid);
        parameters.Add("Alg", alg);
        parameters.Add("PublicJwk", publicJwkJson);
        parameters.Add("PrivateKeyProtected", privateKeyProtected);
        parameters.Add("NotBefore", DateTime.SpecifyKind(notBefore, DateTimeKind.Utc));
        parameters.Add("NotAfter", DateTime.SpecifyKind(notAfter, DateTimeKind.Utc));

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadPrivateKeyProtectedAsync(
        string kid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);

        const string sql = "SELECT signing_keys_read_private(@Kid::text);";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, new { Kid = kid }, cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<byte[]?>(command).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SigningKeyRecord?> GetCurrentAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        // public_jwk::text on the projection: Npgsql 10 materialises jsonb as a JsonDocument
        // by default; the explicit cast to text yields a string the BLL can deserialise
        // through System.Text.Json with the strongly-typed PublicJwk record.
        string sql =
            $"SELECT {PublicSelectColumns} FROM signing_keys "
            + "WHERE not_before <= @Now AND not_after > @Now AND retired_at IS NULL "
            + "ORDER BY created_at DESC LIMIT 1;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new { Now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) },
            cancellationToken: cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SigningKeyRow?>(command).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SigningKeyRecord>> GetTrustedAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        string sql =
            $"SELECT {PublicSelectColumns} FROM signing_keys "
            + "WHERE not_after > @Now AND retired_at IS NULL "
            + "ORDER BY not_after DESC;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new { Now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) },
            cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<SigningKeyRow>(command).ConfigureAwait(false);

        List<SigningKeyRecord> result = [];
        foreach (var r in rows)
        {
            result.Add(ToRecord(r));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> UpdateRetiredAtAsync(
        string kid,
        DateTime retiredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);

        const string sql =
            "UPDATE signing_keys SET retired_at = @RetiredAt "
            + "WHERE kid = @Kid AND retired_at IS NULL;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            sql,
            new { Kid = kid, RetiredAt = DateTime.SpecifyKind(retiredAt, DateTimeKind.Utc) },
            cancellationToken: cancellationToken);
        return await connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    private static SigningKeyRecord ToRecord(SigningKeyRow row)
    {
        var jwk = JsonSerializer.Deserialize<PublicJwk>(row.PublicJwkJson, JwkJsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialise public_jwk for kid {row.Kid}.");
        if (!SigningAlgorithmExtensions.TryParseJwaName(row.Alg, out var alg))
        {
            throw new InvalidOperationException(
                $"Unrecognised alg '{row.Alg}' for kid {row.Kid}; CHECK constraint should have rejected this row.");
        }

        return new SigningKeyRecord(
            row.Kid,
            alg,
            jwk,
            DateTime.SpecifyKind(row.NotBefore, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.NotAfter, DateTimeKind.Utc),
            row.RetiredAt is null
                ? null
                : DateTime.SpecifyKind(row.RetiredAt.Value, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    /// <summary>
    /// Internal materialisation DTO; the public surface returns
    /// <see cref="SigningKeyRecord"/> after deserialising <c>PublicJwkJson</c>.
    /// </summary>
    private sealed class SigningKeyRow
    {
        public string Kid { get; set; } = string.Empty;

        public string Alg { get; set; } = string.Empty;

        public string PublicJwkJson { get; set; } = string.Empty;

        public DateTime NotBefore { get; set; }

        public DateTime NotAfter { get; set; }

        public DateTime? RetiredAt { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.Core.Interfaces;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IOrganizationRepository"/>. Mirrors
/// <see cref="Auditing.AuditEventWriter"/> and <see cref="Identity.HeimdallUserStore"/>
/// in style: an <see cref="NpgsqlConnection"/> per call, <see cref="CommandDefinition"/>
/// for cancellation, and <c>ConfigureAwait(false)</c> on every library-side await.
/// </summary>
/// <remarks>
/// The <c>slug</c> column is <c>citext</c>; Npgsql 10 with raw <see cref="NpgsqlConnection"/>
/// cannot materialise <c>citext</c> directly to <see cref="string"/>, so the SELECT
/// projection casts <c>slug::text AS Slug</c>. Writes (<c>INSERT</c>, <c>UPDATE</c>)
/// remain unchanged because <c>text → citext</c> is implicit on input.
/// </remarks>
public sealed class OrganizationRepository : IOrganizationRepository
{
    private const string SelectColumns =
        "id AS Id, slug::text AS Slug, name AS Name, "
        + "created_at AS CreatedAt, created_by AS CreatedBy";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizationRepository"/> class.
    /// </summary>
    /// <param name="options">Bound <see cref="DataOptions"/> providing the Postgres connection string.</param>
    public OrganizationRepository(IOptions<DataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = options.Value.PostgresConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Organization>> GetAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM organizations ORDER BY slug ASC",
            cancellationToken: cancellationToken
        );
        var rows = await connection.QueryAsync<Organization>(command).ConfigureAwait(false);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<Organization?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM organizations WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<Organization>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Organization?> GetBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        await using var connection = new NpgsqlConnection(_connectionString);
        // Explicit ::citext cast forces Postgres to use the citext = citext operator
        // (case-insensitive). Without the cast, Dapper binds @Slug as text, the
        // planner picks the text = text operator, and the lookup becomes case-
        // sensitive — defeating the entire point of citext.
        var command = new CommandDefinition(
            $"SELECT {SelectColumns} FROM organizations WHERE slug = @Slug::citext",
            new { Slug = slug },
            cancellationToken: cancellationToken
        );
        return await connection
            .QuerySingleOrDefaultAsync<Organization>(command)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(
        Organization organization,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(organization);
        // created_at is sourced from the database clock (column DEFAULT now()) so it
        // is omitted from the INSERT. The RETURNING clause yields both the generated
        // id and the resulting created_at so the caller's entity is fully populated.
        const string sql = @"
INSERT INTO organizations (slug, name, created_by)
VALUES (@Slug, @Name, @CreatedBy)
RETURNING id, created_at;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, organization, cancellationToken: cancellationToken);
        var row = await connection
            .QuerySingleAsync<(Guid Id, DateTimeOffset CreatedAt)>(command)
            .ConfigureAwait(false);
        organization.Id = row.Id;
        organization.CreatedAt = row.CreatedAt;
        return row.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Organization organization,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(organization);
        // created_at and created_by are immutable after INSERT; they are deliberately
        // not in the SET list so a buggy caller can't accidentally rewrite them.
        const string sql = @"
UPDATE organizations
SET slug = @Slug,
    name = @Name
WHERE id = @Id;";

        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(sql, organization, cancellationToken: cancellationToken);
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var command = new CommandDefinition(
            "DELETE FROM organizations WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken
        );
        var rows = await connection.ExecuteAsync(command).ConfigureAwait(false);
        return rows > 0;
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Heimdall.BLL.Authorization.OpenFga;
using Heimdall.Core.Interfaces;
using Npgsql;
namespace Heimdall.Tests.Shared.OpenFga;

/// <summary>
/// Shared test-utility that seeds a standard
/// <c>org → team → project → ticket</c> hierarchy into a real Postgres database
/// and mirrors the corresponding OpenFGA tuples via <see cref="ITupleWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implemented as a <see langword="static"/> class so acceptance- and
/// integration-test fixtures can share the same seeding surface without
/// inheriting from a common base class.
/// </para>
/// <para>
/// All SQL INSERT statements use the same column sets as the production
/// repositories to avoid schema drift. Tuple writes are chunked at the
/// OpenFGA API limit of 100 per call.
/// </para>
/// </remarks>
public static class AuthzSeedingHelper
{
    private const int TupleChunkSize = 100;

    /// <summary>
    /// Seeds a minimal user row directly into the <c>users</c> table and
    /// returns the new <see cref="Guid"/> identity.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection"/>.</param>
    /// <param name="email">Unique e-mail address for the seeded user.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The newly-created user id.</returns>
    public static async Task<Guid> SeedUserAsync(
        NpgsqlConnection connection,
        string email,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        Guid id = Guid.NewGuid();

        await connection.ExecuteAsync(
            new CommandDefinition(
                @"INSERT INTO users
                    (id, email, normalized_email, password_hash,
                     security_stamp, concurrency_stamp, email_confirmed,
                     lockout_end, lockout_enabled, access_failed_count,
                     system_admin, created_at, updated_at)
                  VALUES
                    (@Id, @Email, @NormalizedEmail, @PasswordHash,
                     @SecurityStamp, @ConcurrencyStamp, true,
                     NULL, false, 0,
                     false, now(), now())",
                new
                {
                    Id = id,
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    PasswordHash = "test_seed_hash",
                    SecurityStamp = Guid.NewGuid().ToString(),
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return id;
    }

    /// <summary>
    /// Seeds a complete <c>org → team → project → ticket</c> hierarchy into
    /// <paramref name="connection"/> and writes all corresponding OpenFGA tuples
    /// via <paramref name="tupleWriter"/>.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection"/>.</param>
    /// <param name="tupleWriter">
    /// Live <see cref="ITupleWriter"/> bound to the test's OpenFGA store.
    /// </param>
    /// <param name="creatorUserId">
    /// Id of the user to act as org/team/project admin and ticket creator.
    /// Must already exist in the <c>users</c> table.
    /// </param>
    /// <param name="reporterUserId">
    /// Id of the ticket reporter. Must already exist in the <c>users</c> table.
    /// </param>
    /// <param name="slugPrefix">
    /// Short prefix for generated slug / name values — makes rows identifiable
    /// in the DB across concurrent test runs.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>
    /// A <see cref="SeedResult"/> carrying every generated database id.
    /// </returns>
    public static async Task<SeedResult> SeedAsync(
        NpgsqlConnection connection,
        ITupleWriter tupleWriter,
        Guid creatorUserId,
        Guid reporterUserId,
        string slugPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tupleWriter);
        ArgumentException.ThrowIfNullOrWhiteSpace(slugPrefix);

        // ---- Hierarchy ----
        Guid orgId = await InsertOrganizationAsync(
            connection, creatorUserId, slugPrefix + "-org", cancellationToken).ConfigureAwait(false);

        Guid teamId = await InsertTeamAsync(
            connection, orgId, creatorUserId, slugPrefix + "-team", cancellationToken).ConfigureAwait(false);

        Guid projectId = await InsertProjectAsync(
            connection, teamId, creatorUserId, slugPrefix + "-proj", cancellationToken).ConfigureAwait(false);

        // ---- Memberships ----
        await InsertOrgMemberAsync(connection, orgId, creatorUserId, creatorUserId, "admin", cancellationToken).ConfigureAwait(false);
        await InsertOrgMemberAsync(connection, orgId, reporterUserId, creatorUserId, "member", cancellationToken).ConfigureAwait(false);
        await InsertTeamMemberAsync(connection, teamId, creatorUserId, creatorUserId, "manager", cancellationToken).ConfigureAwait(false);
        await InsertTeamMemberAsync(connection, teamId, reporterUserId, creatorUserId, "member", cancellationToken).ConfigureAwait(false);
        await InsertProjectMemberAsync(connection, projectId, creatorUserId, creatorUserId, "admin", cancellationToken).ConfigureAwait(false);

        // ---- Ticket ----
        int ticketId = await InsertTicketAsync(
            connection, projectId, teamId, reporterUserId, cancellationToken).ConfigureAwait(false);

        // ---- OpenFGA tuples ----
        var tuples = new List<TupleKey>
        {
            TupleShapes.OrgAdmin(orgId, creatorUserId),
            TupleShapes.OrgMember(orgId, reporterUserId),
            TupleShapes.TeamParentOrg(teamId, orgId),
            TupleShapes.TeamAdmin(teamId, creatorUserId),
            TupleShapes.TeamMember(teamId, reporterUserId),
            TupleShapes.ProjectParentTeam(projectId, teamId),
            TupleShapes.ProjectAdmin(projectId, creatorUserId),
            TupleShapes.TicketParentProject(ticketId, projectId),
            TupleShapes.TicketReporter(ticketId, reporterUserId),
        };

        await WriteTuplesChunkedAsync(tupleWriter, tuples, cancellationToken).ConfigureAwait(false);

        return new SeedResult(orgId, teamId, projectId, ticketId, creatorUserId, reporterUserId);
    }

    /// <summary>
    /// Writes <paramref name="tuples"/> to OpenFGA via <paramref name="tupleWriter"/>
    /// in batches of at most <see cref="TupleChunkSize"/> per API call.
    /// </summary>
    /// <param name="tupleWriter">
    /// Tuple writer to use (may be a real <see cref="OpenFgaTupleWriter"/> or a
    /// test double).
    /// </param>
    /// <param name="tuples">Tuples to write.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    public static async Task WriteTuplesChunkedAsync(
        ITupleWriter tupleWriter,
        IReadOnlyList<TupleKey> tuples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tupleWriter);
        ArgumentNullException.ThrowIfNull(tuples);

        for (int offset = 0; offset < tuples.Count; offset += TupleChunkSize)
        {
            int count = Math.Min(TupleChunkSize, tuples.Count - offset);
            var chunk = new TupleKey[count];
            for (int i = 0; i < count; i++)
            {
                chunk[i] = tuples[offset + i];
            }

            await tupleWriter
                .WriteAsync(chunk, Array.Empty<TupleKey>(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ─── Private SQL helpers ──────────────────────────────────────────────────

    private static async Task<Guid> InsertOrganizationAsync(
        NpgsqlConnection connection,
        Guid createdBy,
        string slug,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                @"INSERT INTO organizations (slug, name, created_by)
                  VALUES (@Slug, @Name, @CreatedBy)
                  RETURNING id",
                new { Slug = slug, Name = slug, CreatedBy = createdBy },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<Guid> InsertTeamAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        Guid createdBy,
        string slug,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                @"INSERT INTO teams (organization_id, slug, name, created_by)
                  VALUES (@OrganizationId, @Slug, @Name, @CreatedBy)
                  RETURNING id",
                new { OrganizationId = organizationId, Slug = slug, Name = slug, CreatedBy = createdBy },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<Guid> InsertProjectAsync(
        NpgsqlConnection connection,
        Guid teamId,
        Guid createdBy,
        string slug,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                @"INSERT INTO projects (team_id, slug, name, created_by)
                  VALUES (@TeamId, @Slug, @Name, @CreatedBy)
                  RETURNING id",
                new { TeamId = teamId, Slug = slug, Name = slug, CreatedBy = createdBy },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static Task InsertOrgMemberAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        Guid userId,
        Guid addedBy,
        string role,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(
            new CommandDefinition(
                @"INSERT INTO organization_members (user_id, organization_id, role, added_by)
                  VALUES (@UserId, @OrganizationId, @Role::org_member_role, @AddedBy)",
                new { UserId = userId, OrganizationId = organizationId, Role = role, AddedBy = addedBy },
                cancellationToken: cancellationToken));
    }

    private static Task InsertTeamMemberAsync(
        NpgsqlConnection connection,
        Guid teamId,
        Guid userId,
        Guid addedBy,
        string role,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(
            new CommandDefinition(
                @"INSERT INTO team_members (user_id, team_id, role, added_by)
                  VALUES (@UserId, @TeamId, @Role::team_member_role, @AddedBy)",
                new { UserId = userId, TeamId = teamId, Role = role, AddedBy = addedBy },
                cancellationToken: cancellationToken));
    }

    private static Task InsertProjectMemberAsync(
        NpgsqlConnection connection,
        Guid projectId,
        Guid userId,
        Guid addedBy,
        string role,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(
            new CommandDefinition(
                @"INSERT INTO project_members (user_id, project_id, role, added_by)
                  VALUES (@UserId, @ProjectId, @Role::project_member_role, @AddedBy)",
                new { UserId = userId, ProjectId = projectId, Role = role, AddedBy = addedBy },
                cancellationToken: cancellationToken));
    }

    private static async Task<int> InsertTicketAsync(
        NpgsqlConnection connection,
        Guid projectId,
        Guid teamId,
        Guid reporterId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"INSERT INTO tickets
                    (title, description, status, priority,
                     project_id, team_id, reporter_id, assignee_id,
                     date_created, date_updated)
                  VALUES
                    (@Title, @Description, @Status::smallint, @Priority::smallint,
                     @ProjectId, @TeamId, @ReporterId, NULL,
                     @DateCreated, @DateUpdated)
                  RETURNING id",
                new
                {
                    Title = "Seed ticket",
                    Description = "Seeded by AuthzSeedingHelper for integration tests.",
                    Status = 0,   // TicketStatus.Open
                    Priority = 1, // TicketPriority.Medium
                    ProjectId = projectId,
                    TeamId = teamId,
                    ReporterId = reporterId,
                    DateCreated = now,
                    DateUpdated = now,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

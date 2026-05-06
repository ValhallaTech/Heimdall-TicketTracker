using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Heimdall.DAL.Seeding;

/// <summary>
/// Bulk-inserts deterministic synthetic <c>tickets</c> rows for development and demo data.
/// Intended for direct instantiation by test harnesses and tooling — not registered with
/// the application's DI container.
/// </summary>
/// <remarks>
/// All rows are generated deterministically from the row index, so successive seeding runs
/// produce predictable, reproducible data sets. A single <c>unnest</c>-based
/// <c>INSERT … SELECT</c> statement is used to minimise round-trips to the database.
/// After Phase 2.4 / 2.5 (<c>M202605050020</c>–<c>M202605050025</c>) every ticket carries
/// FK columns to <c>projects</c>, <c>teams</c>, and <c>users</c>; the legacy free-form
/// <c>reporter</c> / <c>assignee</c> varchar columns no longer exist. The synthetic
/// reporter / assignee strings are therefore dropped entirely (per
/// <c>docs/proposals/team-collaboration.md</c> §2 and §9 decision 5) and every seeded
/// ticket is anchored to the default hierarchy created by
/// <c>DefaultHierarchyBootstrapper</c>.
/// </remarks>
public sealed class DatabaseSeeder
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new <see cref="DatabaseSeeder"/> instance.
    /// </summary>
    /// <param name="connectionString">
    /// Npgsql connection string targeting the PostgreSQL database to seed.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="connectionString"/> is <see langword="null"/>, empty, or
    /// whitespace.
    /// </exception>
    public DatabaseSeeder(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>
    /// Bulk-inserts <paramref name="count"/> deterministic synthetic rows into <c>tickets</c>
    /// using a single <c>unnest</c>-based <c>INSERT … SELECT</c> statement. Every row is
    /// anchored to the supplied default project / team / reporter so the post-Phase-2.4 / 2.5
    /// NOT NULL FK constraints on <c>tickets.project_id</c>, <c>tickets.team_id</c>, and
    /// <c>tickets.reporter_id</c> are satisfied.
    /// </summary>
    /// <param name="projectId">FK to seed into <c>tickets.project_id</c> on every row.</param>
    /// <param name="teamId">FK to seed into <c>tickets.team_id</c> on every row.</param>
    /// <param name="reporterId">FK to seed into <c>tickets.reporter_id</c> on every row.</param>
    /// <param name="count">
    /// Number of rows to insert. Must be greater than zero. Defaults to <c>50</c>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the insert has finished.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="count"/> is less than <c>1</c>.
    /// </exception>
    public async Task SeedAsync(
        Guid projectId,
        Guid teamId,
        Guid reporterId,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var titles = new string[count];
        var descriptions = new string[count];
        var statuses = new short[count];
        var priorities = new short[count];
        var datesCreated = new DateTimeOffset[count];
        var datesUpdated = new DateTimeOffset[count];

        // Fixed UTC anchor so every seeding run produces identical timestamps regardless of
        // when it is executed.
        var anchor = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < count; i++)
        {
            titles[i] = $"Sample ticket #{i + 1}";
            descriptions[i] = $"This is the description for sample ticket number {i + 1}.";
            statuses[i] = (short)(i % 4);    // cycles Open / InProgress / Resolved / Closed
            priorities[i] = (short)(i % 4);  // cycles Low / Medium / High / Critical
            datesCreated[i] = anchor.AddHours(-i);
            datesUpdated[i] = anchor.AddHours(-i + (i % 3));
        }

        // project_id / team_id / reporter_id are scalar (one per call); broadcast across
        // every row produced by unnest. assignee_id stays NULL — "unassigned" is a real
        // ticket state per docs/proposals/team-collaboration.md §5.3 and the FK is
        // ON DELETE SET NULL.
        const string sql = """
            INSERT INTO tickets
                (title, description, status, priority,
                 project_id, team_id, reporter_id, assignee_id,
                 date_created, date_updated)
            SELECT t.title, t.description, t.status, t.priority,
                   @ProjectId, @TeamId, @ReporterId, NULL,
                   t.date_created, t.date_updated
            FROM unnest(
                @Titles::varchar[],
                @Descriptions::text[],
                @Statuses::int2[],
                @Priorities::int2[],
                @DatesCreated::timestamptz[],
                @DatesUpdated::timestamptz[]
            ) AS t(title, description, status, priority, date_created, date_updated);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = new CommandDefinition(
            sql,
            new
            {
                ProjectId = projectId,
                TeamId = teamId,
                ReporterId = reporterId,
                Titles = titles,
                Descriptions = descriptions,
                Statuses = statuses,
                Priorities = priorities,
                DatesCreated = datesCreated,
                DatesUpdated = datesUpdated,
            },
            cancellationToken: cancellationToken
        );

        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }
}

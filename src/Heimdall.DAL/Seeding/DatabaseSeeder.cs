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
    /// using a single <c>unnest</c>-based <c>INSERT … SELECT</c> statement.
    /// </summary>
    /// <param name="count">
    /// Number of rows to insert. Must be greater than zero. Defaults to <c>50</c>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the insert has finished.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="count"/> is less than <c>1</c>.
    /// </exception>
    public async Task SeedAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var titles = new string[count];
        var descriptions = new string[count];
        var statuses = new int[count];
        var priorities = new int[count];
        var reporters = new string[count];
        var assignees = new string?[count];
        var datesCreated = new DateTimeOffset[count];
        var datesUpdated = new DateTimeOffset[count];

        // Fixed UTC anchor so every seeding run produces identical timestamps regardless of
        // when it is executed.
        var anchor = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < count; i++)
        {
            titles[i] = $"Sample ticket #{i + 1}";
            descriptions[i] = $"This is the description for sample ticket number {i + 1}.";
            statuses[i] = i % 4;             // cycles Open / InProgress / Resolved / Closed
            priorities[i] = i % 4;           // cycles Low / Medium / High / Critical
            reporters[i] = $"reporter{i % 5}";
            assignees[i] = i % 3 == 0 ? null : $"assignee{i % 4}";
            datesCreated[i] = anchor.AddHours(-i);
            datesUpdated[i] = anchor.AddHours(-i + (i % 3));
        }

        const string sql = """
            INSERT INTO tickets
                (title, description, status, priority, reporter, assignee, date_created, date_updated)
            SELECT * FROM unnest(
                @Titles::varchar[],
                @Descriptions::varchar[],
                @Statuses::int[],
                @Priorities::int[],
                @Reporters::varchar[],
                @Assignees::varchar[],
                @DatesCreated::timestamptz[],
                @DatesUpdated::timestamptz[]
            );
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = new CommandDefinition(
            sql,
            new
            {
                Titles = titles,
                Descriptions = descriptions,
                Statuses = statuses,
                Priorities = priorities,
                Reporters = reporters,
                Assignees = assignees,
                DatesCreated = datesCreated,
                DatesUpdated = datesUpdated,
            },
            cancellationToken: cancellationToken
        );

        await connection.ExecuteAsync(command).ConfigureAwait(false);
    }
}

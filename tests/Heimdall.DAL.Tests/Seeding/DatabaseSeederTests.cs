using Dapper;
using FluentAssertions;
using Heimdall.Core.Models;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Repositories;
using Heimdall.DAL.Seeding;
using Heimdall.DAL.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heimdall.DAL.Tests.Seeding;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DatabaseSeederTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private Guid _projectId;
    private Guid _teamId;
    private Guid _reporterId;

    public DatabaseSeederTests(PostgresFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        await _fx.ResetTicketsAndCollaborationTablesAsync();
        _reporterId = await SeedUserAsync("seeder@example.com");
        var options = Options.Create(new DataOptions { PostgresConnectionString = _fx.ConnectionString });
        var orgRepo = new OrganizationRepository(options);
        var teamRepo = new TeamRepository(options);
        var projectRepo = new ProjectRepository(options);
        var org = new Organization { Slug = "org", Name = "Org", CreatedBy = _reporterId };
        await orgRepo.CreateAsync(org);
        var team = new Team { OrganizationId = org.Id, Slug = "team", Name = "Team", CreatedBy = _reporterId };
        await teamRepo.CreateAsync(team);
        _teamId = team.Id;
        var project = new Project { TeamId = _teamId, Slug = "proj", Name = "Proj", CreatedBy = _reporterId };
        await projectRepo.CreateAsync(project);
        _projectId = project.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedUserAsync(string email)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<Guid>(
            @"INSERT INTO users (email, normalized_email, security_stamp, concurrency_stamp, created_at, updated_at)
              VALUES (@Email, @NormalizedEmail, 's', 'c', now(), now())
              RETURNING id;",
            new { Email = email, NormalizedEmail = email.ToUpperInvariant() });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Throw_When_ConnectionStringIsBlank(string? cs)
    {
        Action act = () => new DatabaseSeeder(cs!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Should_Throw_When_CountIsLessThanOne()
    {
        var seeder = new DatabaseSeeder(_fx.ConnectionString);
        Func<Task> act = () => seeder.SeedAsync(_projectId, _teamId, _reporterId, 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Should_InsertDeterministicRows_When_SeedAsyncCalled()
    {
        var seeder = new DatabaseSeeder(_fx.ConnectionString);

        await seeder.SeedAsync(_projectId, _teamId, _reporterId, 5);

        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var titles = (await conn.QueryAsync<string>("SELECT title FROM tickets ORDER BY id")).ToList();

        titles.Should().HaveCount(5);
        titles[0].Should().Be("Sample ticket #1");
        titles[4].Should().Be("Sample ticket #5");

        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tickets");
        count.Should().Be(5);
    }
}

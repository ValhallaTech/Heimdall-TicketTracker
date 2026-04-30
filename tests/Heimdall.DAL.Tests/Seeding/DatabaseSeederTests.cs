using Dapper;
using FluentAssertions;
using Heimdall.DAL.Seeding;
using Heimdall.DAL.Tests.Infrastructure;
using Npgsql;

namespace Heimdall.DAL.Tests.Seeding;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DatabaseSeederTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public DatabaseSeederTests(PostgresFixture fx) => _fx = fx;

    public Task InitializeAsync() => _fx.ResetTicketsTableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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
        Func<Task> act = () => seeder.SeedAsync(0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Should_InsertDeterministicRows_When_SeedAsyncCalled()
    {
        var seeder = new DatabaseSeeder(_fx.ConnectionString);

        await seeder.SeedAsync(5);

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

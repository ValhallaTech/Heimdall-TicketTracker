using FluentAssertions;
using Heimdall.DAL.Configuration;
using Microsoft.Extensions.Options;

namespace Heimdall.DAL.Tests.Configuration;

public class DataOptionsTests
{
    [Fact]
    public void Should_HaveExpectedSectionName()
    {
        DataOptions.SectionName.Should().Be("Data");
    }

    [Fact]
    public void Should_DefaultToEmptyStrings_When_NewlyCreated()
    {
        var options = new DataOptions();
        options.PostgresConnectionString.Should().BeEmpty();
        options.RedisConnectionString.Should().BeEmpty();
    }

    [Fact]
    public void Should_PersistAssignedValues()
    {
        var options = new DataOptions
        {
            PostgresConnectionString = "Host=h",
            RedisConnectionString = "h:6379",
        };
        options.PostgresConnectionString.Should().Be("Host=h");
        options.RedisConnectionString.Should().Be("h:6379");
    }
}

public class DataOptionsConnectionStringProviderTests
{
    [Fact]
    public void Should_Throw_When_OptionsIsNull()
    {
        Action act = () => new DataOptionsConnectionStringProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_ReturnPostgresConnectionString_When_Resolved()
    {
        var options = Options.Create(new DataOptions { PostgresConnectionString = "Host=h" });
        var provider = new DataOptionsConnectionStringProvider(options);

        provider.GetConnectionString("ignored").Should().Be("Host=h");
        provider.GetConnectionString("ignored", enableMasterSlave: true, readOnly: true).Should().Be("Host=h");
    }
}

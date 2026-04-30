using Dapper.Extensions;
using FluentAssertions;
using Heimdall.DAL.Configuration;
using Heimdall.DAL.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heimdall.DAL.Tests.Extensions;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Should_Throw_When_ServicesIsNull()
    {
        Action act = () => ServiceCollectionExtensions.AddDal(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Should_ReturnSameInstance_When_AddDalCalled()
    {
        var services = new ServiceCollection();

        var result = services.AddDal();

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void Should_RegisterIDapperAndConnectionStringProvider_When_AddDalCalled()
    {
        var services = new ServiceCollection();
        services.Configure<DataOptions>(o => o.PostgresConnectionString =
            "Host=localhost;Database=h;Username=u;Password=p");

        services.AddDal();

        services.Should().Contain(d => d.ServiceType == typeof(IDapper));
        services.Should().Contain(d => d.ServiceType == typeof(IConnectionStringProvider));

        // The connection-string provider should be DataOptions-backed and resolvable.
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IConnectionStringProvider>();
        provider.Should().BeOfType<DataOptionsConnectionStringProvider>();
        provider.GetConnectionString("default").Should().Contain("Host=localhost");
    }
}

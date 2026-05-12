using FluentAssertions;
using Heimdall.Web.Bootstrap;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Heimdall.Web.Tests.Bootstrap;

/// <summary>
/// Unit tests for <see cref="SeedOrganizationHealthProbe"/>. Pins the
/// environment-aware contract: Production throws when no seed-org id resolves;
/// non-Production warns and returns <see cref="Guid.Empty"/>.
/// </summary>
public class SeedOrganizationHealthProbeTests
{
    private readonly IOptionsMonitorCache<SeedOrganizationOptions> _cache =
        new OptionsCache<SeedOrganizationOptions>();

    private static IHostEnvironment Env(string envName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(envName);
        return env.Object;
    }

    private SeedOrganizationHealthProbe Create(IOptionsFactory<SeedOrganizationOptions> factory, string envName) =>
        new(factory, _cache, Env(envName), NullLogger<SeedOrganizationHealthProbe>.Instance);

    private static IOptionsFactory<SeedOrganizationOptions> FactoryReturning(Guid id)
    {
        var factory = new Mock<IOptionsFactory<SeedOrganizationOptions>>();
        factory.Setup(f => f.Create(It.IsAny<string>()))
               .Returns(new SeedOrganizationOptions { OrganizationId = id });
        return factory.Object;
    }

    [Fact]
    public void Should_ReturnResolvedId_When_FactoryProducesNonEmpty()
    {
        Guid expected = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var sut = Create(FactoryReturning(expected), Environments.Production);

        Guid actual = sut.Run();

        actual.Should().Be(expected);
    }

    [Fact]
    public void Should_PublishSnapshotToMonitorCache_AfterRun()
    {
        Guid expected = Guid.NewGuid();
        var sut = Create(FactoryReturning(expected), Environments.Development);

        sut.Run();

        // Monitor cache should now serve the rebuilt snapshot to downstream
        // IOptionsMonitor consumers.
        _cache.GetOrAdd(Options.DefaultName, () => new SeedOrganizationOptions { OrganizationId = Guid.Empty })
              .OrganizationId.Should().Be(expected);
    }

    [Fact]
    public void Should_Throw_When_IdUnresolvedInProduction()
    {
        var sut = Create(FactoryReturning(Guid.Empty), Environments.Production);

        Action act = () => sut.Run();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Seed-organization id was not resolved*");
    }

    [Fact]
    public void Should_ReturnEmpty_When_IdUnresolvedInDevelopment()
    {
        var sut = Create(FactoryReturning(Guid.Empty), Environments.Development);

        Guid actual = sut.Run();

        actual.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Should_ReturnEmpty_When_IdUnresolvedInTestingEnvironment()
    {
        // Acceptance-test factory boots with EnvironmentName="Testing" — must not throw.
        var sut = Create(FactoryReturning(Guid.Empty), "Testing");

        Guid actual = sut.Run();

        actual.Should().Be(Guid.Empty);
    }
}

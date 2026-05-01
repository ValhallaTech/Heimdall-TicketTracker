using FluentAssertions;
using Heimdall.Web.DependencyInjection;

namespace Heimdall.Web.Tests.DependencyInjection;

public class ApplicationModuleTests
{
    [Fact]
    public void Should_CreateInstance_When_DefaultConstructed()
    {
        var module = new ApplicationModule();
        module.Should().NotBeNull();
    }

    [Fact]
    public void Should_Throw_When_LoadCalledWithNullBuilder()
    {
        var module = new ApplicationModule();
        var loadMethod = typeof(ApplicationModule).GetMethod(
            "Load",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Action act = () => loadMethod.Invoke(module, new object?[] { null });

        act.Should()
            .Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<ArgumentNullException>();
    }

    [Fact]
    public void Should_NotThrow_When_LoadCalledWithRealBuilder()
    {
        // Per project convention we do NOT spin up an Autofac container or call Build()
        // in unit tests. We only verify Load() executes without throwing.
        var module = new ApplicationModule();
        var loadMethod = typeof(ApplicationModule).GetMethod(
            "Load",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var builder = new Autofac.ContainerBuilder();

        Action act = () => loadMethod.Invoke(module, new object?[] { builder });

        act.Should().NotThrow();
    }
}

using Heimdall.Tests.Shared.OpenFga;
using Xunit;

namespace Heimdall.BLL.Tests.Authorization.OpenFga.Integration;

/// <summary>
/// Local re-declaration of the <c>OpenFgaIntegration</c> xUnit collection so
/// test classes in this assembly can bind to
/// <see cref="OpenFgaTestcontainersFixture"/> by name. xUnit only discovers
/// <c>[CollectionDefinition]</c> attributes in the same assembly as the
/// decorated <c>[Collection(name)]</c> classes, so the shared declaration in
/// <c>Heimdall.Tests.Shared</c> is not enough on its own.
/// </summary>
[CollectionDefinition(OpenFgaIntegrationCollection.Name, DisableParallelization = true)]
public sealed class BllOpenFgaIntegrationCollection
    : ICollectionFixture<OpenFgaTestcontainersFixture>
{
}

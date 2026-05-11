using Xunit;

namespace Heimdall.Tests.Shared.OpenFga;

/// <summary>
/// xUnit collection that owns the singleton <see cref="OpenFgaTestcontainersFixture"/>
/// for every Phase 3.7 step 12 integration test. Disabling parallelisation here is
/// load-bearing: the fixture's store-wide tuple state would otherwise race across
/// tests (one test's <c>Write</c> would be visible to a sibling on a sub-second
/// boundary).
/// </summary>
/// <remarks>
/// <para>
/// Per <c>docs/proposals/openfga.md</c> §3 step 12 the collection isolates the
/// OpenFGA sidecar stack from the Phase 1 / Phase 2 acceptance suites (which
/// race on process-wide env vars like <c>DATABASE_URL</c>). Tests in different
/// collections still run in parallel by default — this only serialises within
/// the collection.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenFgaIntegrationCollection
    : ICollectionFixture<OpenFgaTestcontainersFixture>
{
    /// <summary>The xUnit collection name.</summary>
    public const string Name = "OpenFgaIntegration";
}

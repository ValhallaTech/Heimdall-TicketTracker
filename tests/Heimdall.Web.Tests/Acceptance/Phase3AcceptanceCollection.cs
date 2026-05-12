using Heimdall.Tests.Shared.OpenFga;
using Heimdall.Web.Tests.Infrastructure;
using Xunit;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// xUnit collection definition for Phase 3 acceptance tests.
/// Uses a dedicated collection name to prevent env-var races with the Phase 1
/// and Phase 2 suites, which share <c>"Phase1Acceptance"</c>.
/// </summary>
[CollectionDefinition("Phase3Acceptance")]
public sealed class Phase3AcceptanceCollection
    : ICollectionFixture<HeimdallWebApplicationFactoryWithOpenFga>
{
}

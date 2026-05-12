using Heimdall.Tests.Shared.OpenFga;
using Heimdall.Web.Tests.Infrastructure;
using Xunit;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// xUnit collection definition for Phase 3 acceptance tests.
/// Uses a dedicated collection name to prevent env-var races with the Phase 1
/// and Phase 2 suites, which share <c>"Phase1Acceptance"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HeimdallWebApplicationFactoryWithOpenFga"/> (and the Phase 1/2
/// acceptance factories) mutate process-wide environment variables during
/// <c>CreateHost</c>. xUnit runs <em>different</em> collections in parallel by
/// default, so simply using a distinct collection name is not sufficient —
/// without <see cref="CollectionDefinitionAttribute.DisableParallelization"/>
/// the Phase 3 fixture could race the Phase 1/2 fixtures on the same env vars.
/// </para>
/// <para>
/// Setting <c>DisableParallelization = true</c> here serializes the Phase 3
/// acceptance suite against every other test collection in this assembly,
/// matching the safety guarantee Phase 1/2 get from sharing a single
/// collection name.
/// </para>
/// </remarks>
[CollectionDefinition("Phase3Acceptance", DisableParallelization = true)]
public sealed class Phase3AcceptanceCollection
    : ICollectionFixture<HeimdallWebApplicationFactoryWithOpenFga>
{
}

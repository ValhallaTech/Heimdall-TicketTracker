using Xunit;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// xUnit collection definition for the OpenAPI gating suites, which share the
/// <c>"Phase5OpenApi"</c> collection name.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="OpenApiGatingFactoryBase"/> variant mutates process-wide
/// environment variables — notably <c>DATABASE_URL</c> — in <c>CreateHost</c>.
/// xUnit runs <em>different</em> collections in parallel by default, so the
/// shared collection name only serializes the gating classes against each
/// other; without
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> they can
/// still race the acceptance collections (which mutate the same env vars) and
/// trigger a flaky <c>23505 duplicate key … pg_type_typname_nsp_index</c> when
/// two hosts migrate the same Postgres database concurrently.
/// </para>
/// <para>
/// Setting <c>DisableParallelization = true</c> serializes this collection
/// against every other collection in the assembly, matching the guarantee
/// <see cref="Heimdall.Web.Tests.Acceptance.Phase3AcceptanceCollection"/>
/// provides for the acceptance suites. The gating test classes attach their own
/// per-class fixtures via <c>IClassFixture&lt;…&gt;</c>, so this definition
/// declares no collection fixture.
/// </para>
/// </remarks>
[CollectionDefinition("Phase5OpenApi", DisableParallelization = true)]
public sealed class Phase5OpenApiCollection
{
}

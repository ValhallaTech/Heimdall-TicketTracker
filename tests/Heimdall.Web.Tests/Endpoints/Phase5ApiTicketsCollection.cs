using Xunit;

namespace Heimdall.Web.Tests.Endpoints;

/// <summary>
/// xUnit collection definition for the Phase 5 <c>/api/v1/tickets/*</c> endpoint
/// suite, which uses the <c>"Phase5ApiTickets"</c> collection name.
/// </summary>
/// <remarks>
/// <para>
/// The suite boots <see cref="Infrastructure.HeimdallWebApplicationFactory"/>,
/// which mutates process-wide environment variables — notably
/// <c>DATABASE_URL</c> — in <c>CreateHost</c>. xUnit runs <em>different</em>
/// collections in parallel by default, so without
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> this
/// collection can race other env-var-mutating collections (the acceptance and
/// OpenAPI-gating suites) and trigger a flaky
/// <c>23505 duplicate key … pg_type_typname_nsp_index</c> when two hosts migrate
/// the same Postgres database concurrently.
/// </para>
/// <para>
/// The endpoint test class attaches its own per-class fixture via
/// <c>IClassFixture&lt;…&gt;</c>, so this definition declares no collection
/// fixture.
/// </para>
/// </remarks>
[CollectionDefinition("Phase5ApiTickets", DisableParallelization = true)]
public sealed class Phase5ApiTicketsCollection
{
}

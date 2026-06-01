using Xunit;

namespace Heimdall.Web.Tests.Acceptance;

/// <summary>
/// xUnit collection definition for the Phase 1, 2, and 5 acceptance suites,
/// which share the <c>"Phase1Acceptance"</c> collection name.
/// </summary>
/// <remarks>
/// <para>
/// The acceptance factories
/// (<see cref="Infrastructure.HeimdallWebApplicationFactory"/> and
/// <see cref="Infrastructure.HeimdallWebApplicationFactoryWithOpenFga"/>) mutate
/// process-wide environment variables — notably <c>DATABASE_URL</c> — during
/// <c>CreateHost</c>. xUnit runs <em>different</em> collections in parallel by
/// default, so sharing a single collection name only serializes these suites
/// against <em>each other</em>; it does not stop them racing other collections
/// (for example the <c>"Phase5OpenApi"</c> gating suite) on the same env vars.
/// </para>
/// <para>
/// A concurrent overwrite of <c>DATABASE_URL</c> lets two hosts run
/// FluentMigrator migrations against the same Postgres database at once, which
/// surfaces as a flaky <c>23505 duplicate key … pg_type_typname_nsp_index</c>
/// while creating the <c>VersionInfo</c> table. Setting
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> serializes
/// this collection against every other collection in the assembly, matching the
/// safety guarantee <see cref="Phase3AcceptanceCollection"/> already provides.
/// </para>
/// <para>
/// The acceptance test classes attach their own per-class fixtures via
/// <c>IClassFixture&lt;…&gt;</c>, so this definition intentionally declares no
/// collection fixture.
/// </para>
/// </remarks>
[CollectionDefinition("Phase1Acceptance", DisableParallelization = true)]
public sealed class Phase1AcceptanceCollection
{
}

using System;

namespace Heimdall.BLL.Authorization.OpenFga;

/// <summary>
/// A single OpenFGA <c>Check</c> request, per <c>docs/proposals/openfga.md</c> §3
/// step 6. Object and user are passed in their fully-qualified forms (e.g.
/// <c>ticket:42</c>, <c>user:11111111-…</c>); see <see cref="TupleShapes"/> for the
/// canonical formatting helpers.
/// </summary>
/// <param name="User">Subject id, including the <c>user:</c> type prefix.</param>
/// <param name="Relation">Relation name as declared in <c>authz/model.fga</c>.</param>
/// <param name="Object">Object id, including the type prefix (e.g. <c>ticket:42</c>).</param>
/// <param name="Consistency">Consistency preference forwarded to the SDK.</param>
public sealed record FgaCheckRequest(
    string User,
    string Relation,
    string Object,
    FgaConsistency Consistency = FgaConsistency.MinimizeLatency);

/// <summary>
/// A single OpenFGA <c>ListObjects</c> request, per <c>docs/proposals/openfga.md</c>
/// §3 step 6. Used for queue page pagination so we ask the sidecar for only the ids
/// the user can already <c>view</c> (avoiding a fetch-then-filter pass over the DB).
/// </summary>
/// <param name="User">Subject id, including the <c>user:</c> type prefix.</param>
/// <param name="Relation">Relation name (typically <c>view</c>).</param>
/// <param name="Type">Object type (e.g. <c>ticket</c>, <c>project</c>).</param>
/// <param name="Consistency">Consistency preference forwarded to the SDK.</param>
public sealed record FgaListObjectsRequest(
    string User,
    string Relation,
    string Type,
    FgaConsistency Consistency = FgaConsistency.MinimizeLatency);

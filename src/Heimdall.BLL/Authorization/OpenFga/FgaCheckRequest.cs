using System;
using System.Collections.Generic;

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

/// <summary>
/// A single OpenFGA <c>ListUsers</c> request, per <c>docs/proposals/openfga.md</c>
/// §3 step 11. Used by the admin "who has access to this ticket" surface to
/// enumerate every <c>user:</c> that holds <paramref name="Relation"/> on the
/// supplied object — including the inheritance walks expressed in
/// <c>authz/model.fga</c> (e.g. org-admin → ticket#view via the parent chain).
/// </summary>
/// <param name="ObjectType">Object type, e.g. <c>ticket</c>.</param>
/// <param name="ObjectId">Object id (without the type prefix). Example: <c>"42"</c> for <c>ticket:42</c>.</param>
/// <param name="Relation">Relation name (typically <c>view</c>).</param>
/// <param name="Consistency">Consistency preference forwarded to the SDK.</param>
public sealed record FgaListUsersRequest(
    string ObjectType,
    string ObjectId,
    string Relation,
    FgaConsistency Consistency = FgaConsistency.MinimizeLatency);

/// <summary>
/// A single OpenFGA <c>Expand</c> request, per <c>docs/proposals/openfga.md</c>
/// §3 step 11. Returns the userset tree for the supplied
/// <c>(object, relation)</c> pair so the admin "why" surface can render the
/// inheritance walk that grants each subject their permission.
/// </summary>
/// <remarks>
/// <see cref="Expand"/> is fixed at <see cref="FgaConsistency.MinimizeLatency"/>
/// (sub-second tree walk; not a read-after-write hot path).
/// </remarks>
/// <param name="ObjectType">Object type, e.g. <c>ticket</c>.</param>
/// <param name="ObjectId">Object id (without the type prefix). Example: <c>"42"</c> for <c>ticket:42</c>.</param>
/// <param name="Relation">Relation name (e.g. <c>view</c>).</param>
public sealed record FgaExpandRequest(
    string ObjectType,
    string ObjectId,
    string Relation);

/// <summary>
/// Clean POCO projection of an OpenFGA <c>Expand</c> response that the admin
/// UI can consume directly without taking a transitive dependency on the SDK
/// model types. The tree carries the userset that grants the queried relation;
/// each leaf is either a set of users, a computed userset reference, or a
/// <c>tuple_to_userset</c> reference. Empty tree (<see cref="Root"/> is
/// <see langword="null"/>) means the relation is unreachable / deny-closed.
/// </summary>
/// <param name="Root">The tree root, or <see langword="null"/> when the relation has no userset.</param>
public sealed record FgaExpandResult(FgaExpandNode? Root);

/// <summary>
/// One node in a <see cref="FgaExpandResult"/> userset tree. Either holds a
/// <see cref="Leaf"/> set, or recurses into <see cref="Union"/> /
/// <see cref="Intersection"/> / <see cref="Difference"/> children. The nesting
/// mirrors the userset operators expressed in the OpenFGA model DSL.
/// </summary>
/// <param name="Name">
/// Fully-qualified node name (e.g. <c>ticket:42#view</c>) when populated by
/// the server; may be empty for synthesised intermediate nodes.
/// </param>
/// <param name="Leaf">Leaf payload, when this node terminates the tree.</param>
/// <param name="Union">Child nodes whose usersets are unioned.</param>
/// <param name="Intersection">Child nodes whose usersets are intersected.</param>
/// <param name="Difference">
/// Two-element <c>(base, subtract)</c> tuple; the resulting userset is
/// <c>base</c> minus <c>subtract</c>. <see langword="null"/> when not a
/// difference node.
/// </param>
public sealed record FgaExpandNode(
    string Name,
    FgaExpandLeaf? Leaf,
    IReadOnlyList<FgaExpandNode>? Union,
    IReadOnlyList<FgaExpandNode>? Intersection,
    FgaExpandDifference? Difference);

/// <summary>
/// A leaf payload in a <see cref="FgaExpandNode"/>. Exactly one of
/// <see cref="Users"/>, <see cref="ComputedUserset"/>, or
/// <see cref="TupleToUserset"/> will be populated.
/// </summary>
/// <param name="Users">
/// User references at this leaf (e.g. <c>user:11111111-…</c>,
/// <c>team:abc#admin</c>). Empty when this is a computed / TTU leaf.
/// </param>
/// <param name="ComputedUserset">
/// Computed userset reference (e.g. <c>project:abc#admin</c>) when present.
/// </param>
/// <param name="TupleToUserset">
/// <c>tuple_to_userset</c> reference (e.g. "<c>parent_project</c> → <c>view</c>")
/// when present.
/// </param>
public sealed record FgaExpandLeaf(
    IReadOnlyList<string> Users,
    string? ComputedUserset,
    FgaExpandTupleToUserset? TupleToUserset);

/// <summary>
/// Body of a <c>tuple_to_userset</c> leaf: the <paramref name="Tupleset"/>
/// relation on the host object is followed, and each yielded userset is
/// expanded against <paramref name="ComputedUsersets"/>.
/// </summary>
/// <param name="Tupleset">The tupleset relation (e.g. <c>parent_project</c>).</param>
/// <param name="ComputedUsersets">The computed usersets evaluated on each yield.</param>
public sealed record FgaExpandTupleToUserset(
    string Tupleset,
    IReadOnlyList<string> ComputedUsersets);

/// <summary>
/// Two-side payload for a difference node: <see cref="Base"/> minus
/// <see cref="Subtract"/>.
/// </summary>
/// <param name="Base">The base userset.</param>
/// <param name="Subtract">The userset subtracted from the base.</param>
public sealed record FgaExpandDifference(FgaExpandNode Base, FgaExpandNode Subtract);

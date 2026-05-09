using System;
using Microsoft.AspNetCore.Authorization;

namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Authorization requirement representing "the acting user holds
/// <see cref="Relation"/> on <see cref="ObjectType"/>:&lt;route value of
/// <see cref="RouteValueKey"/>&gt;" — resolved at request time by
/// <see cref="OpenFgaAuthorizationHandler"/> via
/// <c>IOpenFgaAuthorizationService.CheckAsync</c>. One requirement per named
/// policy in <see cref="AuthorizationPolicies"/>; the handler is policy-agnostic.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RouteValueKey"/> is the name (e.g. <c>"ticketId"</c>) of the route
/// parameter the handler reads to obtain the object id. Allowed to be
/// <see langword="null"/> for the degenerate "no resource bound" case
/// (currently only <see cref="AuthorizationPolicies.SystemAdmin"/>, which is
/// served by <see cref="SystemAdminRequirement"/> — but the shape is preserved
/// here in case a future "tenant index" page needs the same treatment without
/// a route parameter).
/// </para>
/// </remarks>
public sealed class OpenFgaRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenFgaRequirement"/> class.
    /// </summary>
    /// <param name="objectType">
    /// OpenFGA object type (e.g. <c>"ticket"</c>, <c>"team"</c>) — must match
    /// <c>authz/model.fga</c>.
    /// </param>
    /// <param name="relation">
    /// OpenFGA relation name (e.g. <c>"view"</c>, <c>"edit"</c>,
    /// <c>"manage_members"</c>) — must match <c>authz/model.fga</c>.
    /// </param>
    /// <param name="routeValueKey">
    /// Name of the route value carrying the object id. <see langword="null"/>
    /// only for "no resource bound" policies.
    /// </param>
    /// <exception cref="ArgumentException">
    /// If <paramref name="objectType"/> or <paramref name="relation"/> is
    /// <see langword="null"/> or whitespace.
    /// </exception>
    public OpenFgaRequirement(string objectType, string relation, string? routeValueKey)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            throw new ArgumentException("Object type must be non-empty.", nameof(objectType));
        }

        if (string.IsNullOrWhiteSpace(relation))
        {
            throw new ArgumentException("Relation must be non-empty.", nameof(relation));
        }

        ObjectType = objectType;
        Relation = relation;
        RouteValueKey = routeValueKey;
    }

    /// <summary>OpenFGA object type prefix (e.g. <c>"ticket"</c>).</summary>
    public string ObjectType { get; }

    /// <summary>OpenFGA relation name (e.g. <c>"view"</c>).</summary>
    public string Relation { get; }

    /// <summary>
    /// Route-value key (e.g. <c>"ticketId"</c>) used to resolve the object id
    /// at request time, or <see langword="null"/> when the policy does not bind
    /// to a resource.
    /// </summary>
    public string? RouteValueKey { get; }
}

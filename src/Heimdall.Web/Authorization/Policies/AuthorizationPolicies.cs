namespace Heimdall.Web.Authorization.Policies;

/// <summary>
/// Canonical names of every named authorization policy registered in
/// <see cref="AuthorizationConfiguration"/>. Centralised here so call sites
/// (Blazor pages, MVC actions, BLL guards) reference a constant instead of
/// re-typing a magic string — typos surface at compile time, not in a 403.
/// </summary>
/// <remarks>
/// <para>
/// Each policy maps 1:1 onto a relation declared in <c>authz/model.fga</c>;
/// <see cref="AuthorizationConfiguration"/> wires the binding via
/// <see cref="OpenFgaRequirement"/>. The <see cref="SystemAdmin"/> policy is
/// the only one that does <strong>not</strong> consult OpenFGA — it reads the
/// <c>users.system_admin</c> column directly so it remains usable during a
/// sidecar outage (<c>docs/proposals/openfga.md</c> §3 step 10 break-glass
/// path).
/// </para>
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Policy name: actor is authenticated. Named wrapper around
    /// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder.RequireAuthenticatedUser"/>
    /// used on pages whose only gate is a valid session (e.g. <c>Tickets.razor</c>,
    /// <c>NewTicket.razor</c>, <c>Teams/Queue.razor</c>). Explicit rather than relying on the
    /// Phase 1 global fallback policy, which is removed in Phase 3.7 step 14.
    /// </summary>
    public const string IsAuthenticated = nameof(IsAuthenticated);

    /// <summary>Policy name: actor has <c>organization#view</c>.</summary>
    public const string CanViewOrganization = nameof(CanViewOrganization);

    /// <summary>Policy name: actor has <c>organization#manage_members</c>.</summary>
    public const string CanManageOrganizationMembers = nameof(CanManageOrganizationMembers);

    /// <summary>Policy name: actor has <c>team#view</c>.</summary>
    public const string CanViewTeam = nameof(CanViewTeam);

    /// <summary>Policy name: actor has <c>team#manage_members</c>.</summary>
    public const string CanManageTeamMembers = nameof(CanManageTeamMembers);

    /// <summary>Policy name: actor has <c>project#view</c>.</summary>
    public const string CanViewProject = nameof(CanViewProject);

    /// <summary>Policy name: actor has <c>project#edit</c>.</summary>
    public const string CanEditProject = nameof(CanEditProject);

    /// <summary>Policy name: actor has <c>project#manage_members</c>.</summary>
    public const string CanManageProjectMembers = nameof(CanManageProjectMembers);

    /// <summary>Policy name: actor has <c>ticket#view</c>.</summary>
    public const string CanViewTicket = nameof(CanViewTicket);

    /// <summary>Policy name: actor has <c>ticket#edit</c>.</summary>
    public const string CanEditTicket = nameof(CanEditTicket);

    /// <summary>Policy name: actor has <c>ticket#comment</c>.</summary>
    public const string CanCommentTicket = nameof(CanCommentTicket);

    /// <summary>Policy name: actor has <c>ticket#assign</c>.</summary>
    public const string CanAssignTicket = nameof(CanAssignTicket);

    /// <summary>
    /// Policy name: actor's <c>users.system_admin</c> column is <c>true</c>.
    /// Bypasses OpenFGA so it remains usable during a sidecar outage. Used by
    /// every <c>/admin/*</c> Blazor page.
    /// </summary>
    public const string SystemAdmin = nameof(SystemAdmin);

    /// <summary>
    /// Policy name: actor has satisfied the MFA gate. Applied to admin surfaces
    /// in Phase 4.6 step 18. The handler currently registered in Phase 4.3
    /// step 8 is a fail-closed placeholder (see
    /// <see cref="RequireMfaPlaceholderAuthorizationHandler"/>); the real
    /// OpenFGA + <c>amr</c>-aware handler lands in Phase 4.6 step 16.
    /// </summary>
    public const string RequireMfa = nameof(RequireMfa);

    /// <summary>Route value name carrying an organization id.</summary>
    public const string OrganizationIdRouteKey = "organizationId";

    /// <summary>Route value name carrying a team id.</summary>
    public const string TeamIdRouteKey = "teamId";

    /// <summary>Route value name carrying a project id.</summary>
    public const string ProjectIdRouteKey = "projectId";

    /// <summary>Route value name carrying a ticket id.</summary>
    public const string TicketIdRouteKey = "ticketId";
}
